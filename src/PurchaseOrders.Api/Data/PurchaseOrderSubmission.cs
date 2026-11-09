namespace PurchaseOrders.Api.Data;

public sealed class PurchaseOrderSubmission
{
    public Guid Id { get; init; }
    public required string RequestPayload { get; init; }
    public Guid PurchaseOrderId { get; init; }
    public string? ResponseJson { get; init; }
}
