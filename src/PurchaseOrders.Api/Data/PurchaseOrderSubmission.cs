namespace PurchaseOrders.Api.Data;

public sealed class PurchaseOrderSubmission
{
    public Guid Id { get; set; }
    public required string RequestPayload { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public string? ResponseJson { get; set; }
}
