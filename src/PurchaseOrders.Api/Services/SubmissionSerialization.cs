using System.Globalization;
using System.Text.Json;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.Api.Services;

public static class SubmissionSerialization
{
    // Web options also read the retained PascalCase payloads with quoted prices.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string SerializeResponse(PurchaseOrderDto order) => JsonSerializer.Serialize(order, Options);

    public static PurchaseOrderDto ReadResponse(string json) =>
        JsonSerializer.Deserialize<PurchaseOrderDto>(json, Options)
        ?? throw new InvalidOperationException("The saved submission response is empty.");

    public static string NormalizePayload(string json) => NormalizePayload(
        JsonSerializer.Deserialize<CreatePurchaseOrderRequest>(json, Options)
        ?? throw new InvalidOperationException("The saved submission payload is empty."));

    public static string NormalizePayload(CreatePurchaseOrderRequest request) => JsonSerializer.Serialize(new
    {
        SupplierCode = request.SupplierCode.Trim().ToUpperInvariant(),
        Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
        Lines = request.Lines.Select(line => new
        {
            Sku = line.Sku.Trim().ToUpperInvariant(),
            Description = line.Description.Trim(),
            line.Quantity,
            UnitPrice = line.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture),
        }),
    });
}
