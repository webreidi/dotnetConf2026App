using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PurchaseOrders.Api.Data;
using PurchaseOrders.Api.Domain;
using PurchaseOrders.Api.Observability;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.Api.Services;

public abstract record CreateDraftResult
{
    public sealed record Created(PurchaseOrderDto Order) : CreateDraftResult;
    public sealed record Invalid(IDictionary<string, string[]> Errors) : CreateDraftResult;
    public sealed record Conflict : CreateDraftResult;
}

public sealed class PurchaseOrderService(
    PurchasingDbContext db,
    ISupplierOrderSimulator supplierSimulator,
    TimeProvider timeProvider,
    ILogger<PurchaseOrderService> logger)
{
    public async Task<IReadOnlyList<SupplierDto>> GetActiveSuppliersAsync(CancellationToken cancellationToken) =>
        await db.Suppliers
            .AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new SupplierDto(s.Code, s.Name, s.Currency))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListAsync(string? supplierCode, int take, CancellationToken cancellationToken)
    {
        var query = db.PurchaseOrders.AsNoTracking().Include(o => o.Lines).AsQueryable();
        if (!string.IsNullOrWhiteSpace(supplierCode))
        {
            var code = supplierCode.Trim().ToUpperInvariant();
            query = query.Where(o => o.SupplierCode == code);
        }

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return orders
            .Select(o => new PurchaseOrderSummaryDto(o.Id, o.OrderNumber, o.SupplierCode, o.Status.ToString(), o.Total, o.Lines.Count, o.CreatedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<PurchaseOrderDto>> ListDuplicatesAsync(CancellationToken cancellationToken)
        => (await ListDuplicateGroupsAsync(cancellationToken))
            .SelectMany(group => group.Orders)
            .ToList();

    public async Task<IReadOnlyList<PurchaseOrderDuplicateGroupDto>> ListDuplicateGroupsAsync(CancellationToken cancellationToken)
    {
        var orders = await db.PurchaseOrders
            .AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Supplier)
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return PurchaseOrderDuplicateDetector.FindDuplicateGroups(orders.Select(o => o.ToDto()));
    }

    public async Task<PurchaseOrderDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.PurchaseOrders
            .AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Supplier)
            .SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

        return order is null ? null : order.ToDto();
    }

    public async Task<CreateDraftResult> CreateDraftAsync(Guid submissionId, CreatePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        using var activity = PurchaseOrderTelemetry.ActivitySource.StartActivity("purchase_order.create_draft");
        var errors = PurchaseOrderValidator.Validate(request);
        if (errors.Count > 0)
        {
            RecordDraftCreation(activity, "invalid");
            return new CreateDraftResult.Invalid(errors);
        }

        if (submissionId == Guid.Empty)
        {
            return new CreateDraftResult.Invalid(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = ["A nonempty UUID submission identity is required."],
            });
        }

        var payload = SubmissionSerialization.NormalizePayload(request);
        var persisted = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // A retry after an ambiguous commit must re-read the ledger, not re-save tracked inserts.
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({submissionId.ToString("D")}, 0))", cancellationToken);
            var saved = await db.Submissions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
            if (saved is not null)
            {
                if (SubmissionSerialization.NormalizePayload(saved.RequestPayload) != payload)
                {
                    logger.LogWarning("Conflicting payload for submission {SubmissionId}", submissionId);
                    return (Result: (CreateDraftResult)new CreateDraftResult.Conflict(), NewOrder: (PurchaseOrder?)null);
                }

                var response = SubmissionSerialization.ReadResponse(saved.ResponseJson
                    ?? throw new InvalidOperationException("The submission response was not initialized."));
                return (Result: (CreateDraftResult)new CreateDraftResult.Created(response), NewOrder: (PurchaseOrder?)null);
            }

            var supplierCode = request.SupplierCode.Trim().ToUpperInvariant();
            var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Code == supplierCode, cancellationToken);
            if (supplier is null || !supplier.IsActive)
            {
                RecordDraftCreation(activity, "supplier_unavailable");
                return (Result: (CreateDraftResult)new CreateDraftResult.Invalid(new Dictionary<string, string[]>
                {
                    [nameof(request.SupplierCode)] = [$"Supplier '{supplierCode}' was not found or is inactive."],
                }), NewOrder: (PurchaseOrder?)null);
            }

            var now = timeProvider.GetUtcNow();
            var order = PurchaseOrder.CreateDraft(
                supplier,
                OrderNumberGenerator.Create(now),
                request.Notes,
                request.Lines.Select(l => new NewPurchaseOrderLine(l.Sku, l.Description, l.Quantity, l.UnitPrice)),
                now);

            db.PurchaseOrders.Add(order);
            var dto = order.ToDto();
            db.Submissions.Add(new PurchaseOrderSubmission
            {
                Id = submissionId,
                RequestPayload = payload,
                PurchaseOrderId = order.Id,
                ResponseJson = SubmissionSerialization.SerializeResponse(dto),
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (Result: (CreateDraftResult)new CreateDraftResult.Created(dto), NewOrder: (PurchaseOrder?)order);
        });

        if (persisted.NewOrder is not { } createdOrder)
        {
            return persisted.Result;
        }
        RecordDraftCreation(activity, "created");
        logger.LogInformation("Created draft purchase order {OrderNumber} for {Supplier} totaling {Total}",
            createdOrder.OrderNumber, createdOrder.SupplierCode, createdOrder.Total);

        await supplierSimulator.PrepareDraftSubmissionAsync(createdOrder, cancellationToken);

        return persisted.Result;
    }

    private static void RecordDraftCreation(Activity? activity, string outcome)
    {
        activity?.SetTag("purchase_order.outcome", outcome);
        PurchaseOrderTelemetry.RecordDraftCreation(outcome);
    }
}

public static class PurchaseOrderMapping
{
    public static PurchaseOrderDto ToDto(this PurchaseOrder order) => new(
        order.Id,
        order.OrderNumber,
        order.SupplierCode,
        order.Supplier?.Name ?? order.SupplierCode,
        order.Status.ToString(),
        order.Notes,
        order.Total,
        order.CreatedAt,
        order.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => new PurchaseOrderLineDto(l.LineNumber, l.Sku, l.Description, l.Quantity, l.UnitPrice, l.LineTotal))
            .ToList());
}
