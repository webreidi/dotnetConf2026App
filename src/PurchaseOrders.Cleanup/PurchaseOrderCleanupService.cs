using System.Data;
using Microsoft.EntityFrameworkCore;
using PurchaseOrders.Api.Data;
using PurchaseOrders.Api.Domain;
using PurchaseOrders.Api.Services;

namespace PurchaseOrders.Cleanup;

public sealed record CleanupPlan(
    int GroupCount,
    int ReportedRecordCount,
    IReadOnlyList<CleanupPair> Pairs,
    int ExpectedLineDeletionCount);

public sealed record CleanupResult(int DeletedPurchaseOrders, int DeletedLines, int PreservedCanonicalOrders);

public sealed record CleanupIssue(Guid? DuplicateId, Guid? CanonicalId, string Message);

public sealed class CleanupValidationException(IReadOnlyList<CleanupIssue> issues)
    : InvalidOperationException("The report no longer safely describes the target database.")
{
    public IReadOnlyList<CleanupIssue> Issues { get; } = issues;
}

public sealed class CleanupExecutionException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);

public sealed class PurchaseOrderCleanupService(PurchasingDbContext db)
{
    public async Task<CleanupPlan> PreviewAsync(
        ParsedDuplicatePurchaseOrderReport report,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var state = await ValidateAsync(report, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return state.Plan;
    }

    public async Task<CleanupResult> ExecuteAsync(
        ParsedDuplicatePurchaseOrderReport report,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        ValidatedState state;
        var commitStarted = false;
        try
        {
            state = await ValidateAsync(report, cancellationToken);
            var duplicateIds = report.Pairs.Select(pair => pair.DuplicateId).ToArray();
            var duplicates = state.Orders.Where(order => duplicateIds.Contains(order.Id)).ToArray();
            db.PurchaseOrders.RemoveRange(duplicates);
            await db.SaveChangesAsync(cancellationToken);

            var remainingOrders = await db.PurchaseOrders.AsNoTracking()
                .AnyAsync(order => duplicateIds.Contains(order.Id), cancellationToken);
            var remainingLines = await db.Set<PurchaseOrderLine>().AsNoTracking()
                .AnyAsync(line => duplicateIds.Contains(line.PurchaseOrderId), cancellationToken);
            var canonicalIds = report.CanonicalIds.ToArray();
            var remainingCanonicals = await db.PurchaseOrders.AsNoTracking()
                .CountAsync(order => canonicalIds.Contains(order.Id), cancellationToken);
            if (remainingOrders || remainingLines || remainingCanonicals != canonicalIds.Length)
            {
                throw new InvalidOperationException(
                    "Post-delete verification found a remaining duplicate/line or a missing canonical record.");
            }

            commitStarted = true;
            await transaction.CommitAsync(cancellationToken);
            return new CleanupResult(duplicates.Length, state.Plan.ExpectedLineDeletionCount, remainingCanonicals);
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is not CleanupExecutionException)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                throw new CleanupExecutionException(
                    "Cleanup failed and rollback could not be confirmed; inspect the disposable target before retrying.",
                    new AggregateException(ex, rollbackException));
            }

            if (ex is CleanupValidationException)
            {
                throw;
            }
            throw new CleanupExecutionException(
                commitStarted
                    ? "Commit acknowledgement failed; the database outcome is unknown."
                    : "Cleanup transaction failed and was rolled back.",
                ex);
        }
    }

    private async Task<ValidatedState> ValidateAsync(
        ParsedDuplicatePurchaseOrderReport report,
        CancellationToken cancellationToken)
    {
        var orders = await db.PurchaseOrders
            .Include(order => order.Lines)
            .Include(order => order.Supplier)
            .OrderBy(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var ordersById = orders.ToDictionary(order => order.Id);
        var issues = new List<CleanupIssue>();

        foreach (var (id, snapshot) in report.Orders)
        {
            if (!ordersById.TryGetValue(id, out var actual))
            {
                AddIssuesForOrder(report, issues, id, $"Reported order {id:D} is missing from the database.");
                continue;
            }

            if (!MatchesSnapshot(actual, snapshot))
            {
                AddIssuesForOrder(report, issues, id, $"Reported order {id:D} changed after the report was generated.");
            }
        }

        var actualGroups = PurchaseOrderDuplicateDetector.FindDuplicateGroups(
            orders.Select(PurchaseOrderMapping.ToDto));
        var reportedGroups = report.CanonicalIds.ToDictionary(
            canonicalId => canonicalId,
            canonicalId => report.Pairs
                .Where(pair => pair.CanonicalId == canonicalId)
                .Select(pair => pair.DuplicateId)
                .Prepend(canonicalId)
                .ToHashSet());
        var actualGroupIds = actualGroups
            .Select(group => group.Orders.Select(order => order.Id).ToHashSet())
            .ToArray();
        var expectedGroupIds = reportedGroups.Values.ToArray();
        var groupMismatch = actualGroupIds.Length != expectedGroupIds.Length ||
            expectedGroupIds.Any(expected => !actualGroupIds.Any(actual => actual.SetEquals(expected)));
        if (groupMismatch)
        {
            foreach (var canonicalId in report.CanonicalIds)
            {
                AddGroupIssue(report, issues, canonicalId,
                    $"The current duplicate group for canonical {canonicalId:D} differs from the report.");
            }
            if (actualGroupIds.Any(actual => !expectedGroupIds.Any(expected => expected.SetEquals(actual))))
            {
                issues.Add(new CleanupIssue(null, null, "The database contains an unreported duplicate group."));
            }
        }

        foreach (var group in actualGroups)
        {
            var canonical = group.Orders
                .OrderBy(order => order.CreatedAt)
                .ThenBy(order => order.Id.ToString("D"), StringComparer.Ordinal)
                .First();
            if (!reportedGroups.TryGetValue(canonical.Id, out var expectedIds) ||
                !expectedIds.SetEquals(group.Orders.Select(order => order.Id)))
            {
                continue;
            }

            var orderedIds = group.Orders
                .OrderBy(order => order.CreatedAt)
                .ThenBy(order => order.Id.ToString("D"), StringComparer.Ordinal)
                .Select(order => order.Id)
                .ToArray();
            var reportedOrderIds = report.Orders.Values
                .Where(order => expectedIds.Contains(order.Id))
                .OrderBy(order => order.CreatedAt)
                .ThenBy(order => order.Id.ToString("D"), StringComparer.Ordinal)
                .Select(order => order.Id)
                .ToArray();
            if (!orderedIds.SequenceEqual(reportedOrderIds) || canonical.Id != report.CanonicalIds
                    .SingleOrDefault(id => expectedIds.Contains(id)))
            {
                AddGroupIssue(report, issues, canonical.Id,
                    $"Canonical selection for duplicate group {canonical.Id:D} changed.");
            }
        }

        var duplicateIds = report.Pairs.Select(pair => pair.DuplicateId).ToArray();
        var linkedSubmissionIds = await db.PurchaseOrderSubmissions.AsNoTracking()
            .Where(submission => duplicateIds.Contains(submission.PurchaseOrderId))
            .Select(submission => submission.PurchaseOrderId)
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var linkedId in linkedSubmissionIds)
        {
            AddIssuesForOrder(report, issues, linkedId,
                $"Order {linkedId:D} has a durable submission identity; deletion is restricted to preserve replay history.");
        }

        if (issues.Count > 0)
        {
            throw new CleanupValidationException(issues);
        }

        var duplicateOrders = report.Pairs
            .Select(pair => ordersById[pair.DuplicateId])
            .ToArray();
        return new ValidatedState(
            orders,
            new CleanupPlan(
                report.GroupCount,
                report.ReportedRecordCount,
                report.Pairs,
                duplicateOrders.Sum(order => order.Lines.Count)));
    }

    private static bool MatchesSnapshot(PurchaseOrder actual, CleanupOrderSnapshot expected)
    {
        if (actual.Id != expected.Id ||
            actual.OrderNumber != expected.OrderNumber ||
            actual.SupplierCode != expected.SupplierCode ||
            actual.Supplier?.Name != expected.SupplierName ||
            actual.Status.ToString() != expected.Status ||
            actual.Notes != expected.Notes ||
            actual.Total != expected.Total ||
            !actual.CreatedAt.EqualsExact(expected.CreatedAt))
        {
            return false;
        }

        var lines = actual.Lines.OrderBy(line => line.LineNumber).ToArray();
        return lines.Length == expected.Lines.Count &&
            lines.Zip(expected.Lines).All(pair =>
                pair.First.LineNumber == pair.Second.LineNumber &&
                pair.First.Sku == pair.Second.Sku &&
                pair.First.Description == pair.Second.Description &&
                pair.First.Quantity == pair.Second.Quantity &&
                pair.First.UnitPrice == pair.Second.UnitPrice &&
                pair.First.LineTotal == pair.Second.LineTotal);
    }

    private static void AddIssuesForOrder(
        ParsedDuplicatePurchaseOrderReport report,
        ICollection<CleanupIssue> issues,
        Guid orderId,
        string message)
    {
        var pairs = report.Pairs.Where(pair => pair.DuplicateId == orderId || pair.CanonicalId == orderId).ToArray();
        if (pairs.Length == 0)
        {
            issues.Add(new CleanupIssue(null, null, message));
            return;
        }
        foreach (var pair in pairs)
        {
            issues.Add(new CleanupIssue(pair.DuplicateId, pair.CanonicalId, message));
        }
    }

    private static void AddGroupIssue(
        ParsedDuplicatePurchaseOrderReport report,
        ICollection<CleanupIssue> issues,
        Guid canonicalId,
        string message)
    {
        var pairs = report.Pairs.Where(pair => pair.CanonicalId == canonicalId).ToArray();
        if (pairs.Length == 0)
        {
            issues.Add(new CleanupIssue(null, canonicalId, message));
            return;
        }
        foreach (var pair in pairs)
        {
            issues.Add(new CleanupIssue(pair.DuplicateId, pair.CanonicalId, message));
        }
    }

    private sealed record ValidatedState(IReadOnlyList<PurchaseOrder> Orders, CleanupPlan Plan);
}
