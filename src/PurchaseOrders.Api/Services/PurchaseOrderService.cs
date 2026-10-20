using Microsoft.EntityFrameworkCore;
using PurchaseOrders.Api.Data;
using PurchaseOrders.Api.Domain;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.Api.Services;

public abstract record CreateDraftResult
{
    public sealed record Created(PurchaseOrderDto Order) : CreateDraftResult;
    public sealed record Invalid(IDictionary<string, string[]> Errors) : CreateDraftResult;
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

    public async Task<PurchaseOrderDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.PurchaseOrders
            .AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Supplier)
            .SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

        return order is null ? null : order.ToDto();
    }

    public async Task<CreateDraftResult> CreateDraftAsync(CreatePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        var errors = PurchaseOrderValidator.Validate(request);
        if (errors.Count > 0)
        {
            return new CreateDraftResult.Invalid(errors);
        }

        var supplierCode = request.SupplierCode.Trim().ToUpperInvariant();
        var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Code == supplierCode, cancellationToken);
        if (supplier is null || !supplier.IsActive)
        {
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
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created draft purchase order {OrderNumber} for {Supplier} totaling {Total}",
            order.OrderNumber, order.SupplierCode, order.Total);

        await supplierSimulator.PrepareDraftSubmissionAsync(order, cancellationToken);

        return new CreateDraftResult.Created(order.ToDto());
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
