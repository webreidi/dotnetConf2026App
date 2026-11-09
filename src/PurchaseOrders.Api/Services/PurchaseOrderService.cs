using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
    private static readonly JsonSerializerOptions StoredJsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

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

    public async Task<IReadOnlyList<PurchaseOrderDto>> ListDuplicatesAsync(CancellationToken cancellationToken) =>
        PurchaseOrderDuplicateDetector.FindDuplicates(await ListAllPurchaseOrdersAsync(cancellationToken));

    public async Task<IReadOnlyList<DuplicatePurchaseOrderGroupDto>> ListDuplicateGroupsAsync(CancellationToken cancellationToken) =>
        PurchaseOrderDuplicateDetector.FindDuplicateGroups(await ListAllPurchaseOrdersAsync(cancellationToken));

    private async Task<IReadOnlyList<PurchaseOrderDto>> ListAllPurchaseOrdersAsync(CancellationToken cancellationToken)
    {
        var orders = await db.PurchaseOrders
            .AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Supplier)
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return orders.Select(o => o.ToDto()).ToList();
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

    public async Task<CreateDraftResult> CreateDraftAsync(CreatePurchaseOrderRequest request, Guid submissionId, CancellationToken cancellationToken)
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
                ["Idempotency-Key"] = ["A non-empty submission UUID is required."],
            });
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // A previous attempt may have committed even if the acknowledgement was lost.
            db.ChangeTracker.Clear();
            var replay = await ResolveReplayAsync(submissionId, request, cancellationToken);
            if (replay is not null)
            {
                RecordDraftCreation(activity, replay is CreateDraftResult.Conflict ? "conflict" : "replayed");
                return replay;
            }

            return await CreateOnceAsync(request, submissionId, activity, cancellationToken);
        });
    }

    private async Task<CreateDraftResult> CreateOnceAsync(
        CreatePurchaseOrderRequest request, Guid submissionId, Activity? activity, CancellationToken cancellationToken)
    {
        var supplierCode = request.SupplierCode.Trim().ToUpperInvariant();
        var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Code == supplierCode, cancellationToken);
        if (supplier is null || !supplier.IsActive)
        {
            RecordDraftCreation(activity, "supplier_unavailable");
            return new CreateDraftResult.Invalid(new Dictionary<string, string[]>
            {
                [nameof(request.SupplierCode)] = [$"Supplier '{supplierCode}' was not found or is inactive."],
            });
        }

        var now = timeProvider.GetUtcNow();
        var order = PurchaseOrder.CreateDraft(
            supplier,
            OrderNumberGenerator.Create(now),
            request.Notes,
            request.Lines.Select(l => new NewPurchaseOrderLine(l.Sku, l.Description, l.Quantity, l.UnitPrice)),
            now);

        db.PurchaseOrders.Add(order);
        var response = order.ToDto();
        db.PurchaseOrderSubmissions.Add(new PurchaseOrderSubmission
        {
            Id = submissionId,
            RequestPayload = CanonicalPayload(request),
            PurchaseOrderId = order.Id,
            ResponseJson = JsonSerializer.Serialize(response, StoredJsonOptions),
        });
        try
        {
            // EF's implicit transaction commits the order, lines, and unique identity together.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
               { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_purchase_order_submissions" })
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("Resolving concurrent replay of submission {SubmissionId}", submissionId);
            return await ResolveReplayAsync(submissionId, request, cancellationToken)
                ?? throw new InvalidOperationException("The conflicting submission was not found.", ex);
        }

        RecordDraftCreation(activity, "created");
        logger.LogInformation("Created draft purchase order {OrderNumber} for {Supplier} totaling {Total}",
            order.OrderNumber, order.SupplierCode, order.Total);

        await supplierSimulator.PrepareDraftSubmissionAsync(order, cancellationToken);

        return new CreateDraftResult.Created(response);
    }

    private async Task<CreateDraftResult?> ResolveReplayAsync(
        Guid submissionId, CreatePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        var submission = await db.PurchaseOrderSubmissions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (submission is null)
        {
            return null;
        }

        var originalRequest = JsonSerializer.Deserialize<CreatePurchaseOrderRequest>(submission.RequestPayload, StoredJsonOptions)
            ?? throw new InvalidOperationException($"Submission {submissionId} has no stored request.");
        if (CanonicalPayload(originalRequest) != CanonicalPayload(request))
        {
            logger.LogWarning("Conflicting payload for submission {SubmissionId}", submissionId);
            return new CreateDraftResult.Conflict();
        }

        if (submission.ResponseJson is { } json)
        {
            return new CreateDraftResult.Created(JsonSerializer.Deserialize<PurchaseOrderDto>(json, StoredJsonOptions)
                ?? throw new InvalidOperationException($"Submission {submissionId} has no stored response."));
        }

        logger.LogWarning("Submission {SubmissionId} predates response snapshots; resolving saved order", submissionId);
        var recoveredOrder = await GetAsync(submission.PurchaseOrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Submission {submissionId} references a missing order.");
        var recoveredJson = JsonSerializer.Serialize(recoveredOrder, StoredJsonOptions);
        // Freeze the first reconstruction. Concurrent replays must all return the winning snapshot.
        await db.PurchaseOrderSubmissions
            .Where(s => s.Id == submissionId && s.ResponseJson == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.ResponseJson, recoveredJson), cancellationToken);
        var savedJson = await db.PurchaseOrderSubmissions.AsNoTracking()
            .Where(s => s.Id == submissionId)
            .Select(s => s.ResponseJson)
            .SingleAsync(cancellationToken);
        return new CreateDraftResult.Created(JsonSerializer.Deserialize<PurchaseOrderDto>(
            savedJson ?? throw new InvalidOperationException($"Submission {submissionId} has no recovered response."),
            StoredJsonOptions) ?? throw new InvalidOperationException($"Submission {submissionId} has no stored response."));
    }

    private static string CanonicalPayload(CreatePurchaseOrderRequest request) =>
        JsonSerializer.Serialize(new
        {
            SupplierCode = request.SupplierCode.Trim().ToUpperInvariant(),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            Lines = request.Lines.Select(l => new
            {
                Sku = l.Sku.Trim().ToUpperInvariant(),
                Description = l.Description.Trim(),
                l.Quantity,
                UnitPrice = l.UnitPrice.ToString("G29", CultureInfo.InvariantCulture),
            }),
        });

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
