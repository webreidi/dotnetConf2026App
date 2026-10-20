namespace PurchaseOrders.Contracts;

public sealed record SupplierDto(string Code, string Name, string Currency);

public sealed record PurchaseOrderLineRequest(string Sku, string Description, int Quantity, decimal UnitPrice);

public sealed record CreatePurchaseOrderRequest(
    string SupplierCode,
    string? Notes,
    IReadOnlyList<PurchaseOrderLineRequest> Lines);

public sealed record PurchaseOrderLineDto(
    int LineNumber,
    string Sku,
    string Description,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record PurchaseOrderDto(
    Guid Id,
    string OrderNumber,
    string SupplierCode,
    string SupplierName,
    string Status,
    string? Notes,
    decimal Total,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseOrderLineDto> Lines);

public sealed record PurchaseOrderSummaryDto(
    Guid Id,
    string OrderNumber,
    string SupplierCode,
    string Status,
    decimal Total,
    int LineCount,
    DateTimeOffset CreatedAt);
