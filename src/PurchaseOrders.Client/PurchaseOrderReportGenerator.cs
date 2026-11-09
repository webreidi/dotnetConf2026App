using System.Globalization;
using System.Text.Json;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.Client;

public static class PurchaseOrderReportGenerator
{
    public static async Task<string> GenerateDuplicatesAsync(
        PurchaseOrdersApiClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        cancellationToken.ThrowIfCancellationRequested();

        var groups = await client.ListDuplicatePurchaseOrderGroupsAsync(cancellationToken);
        var reportGroups = groups
            .Select(group =>
            {
                var orders = group.Orders
                    .OrderBy(order => order.CreatedAt)
                    .ThenBy(order => order.Id.ToString("D"), StringComparer.Ordinal)
                    .ToArray();
                if (orders.Length < 2)
                {
                    throw new InvalidOperationException("The duplicate-purchase-orders API returned a group with fewer than two orders.");
                }

                var canonical = orders[0];
                return new
                {
                    CanonicalCreatedAt = canonical.CreatedAt,
                    CanonicalId = canonical.Id,
                    Report = new
                    {
                        canonicalId = canonical.Id,
                        duplicatePairs = orders.Skip(1).Select(order => new
                        {
                            duplicateId = order.Id,
                            canonicalId = canonical.Id,
                        }).ToArray(),
                        orders = orders.Select(order => new
                        {
                            notice = "Saved draft purchase order for review only. This draft has not been sent to suppliers.",
                            order,
                        }).ToArray(),
                    },
                };
            })
            .OrderBy(group => group.CanonicalCreatedAt)
            .ThenBy(group => group.CanonicalId.ToString("D"), StringComparer.Ordinal)
            .Select(group => group.Report)
            .ToArray();

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.WriteLine("# Duplicate purchase orders report");
        writer.WriteLine();
        writer.WriteLine("Matches compare supplier code and name, status, notes, total, and every line item; PO ID, generated order number, and creation timestamp are excluded.");
        writer.WriteLine("For review, the canonical record is the earliest-created order; ties are broken by ordinal UUID text. This choice is for review only, not proof of which record was original.");
        writer.WriteLine("These saved drafts have not been sent to suppliers.");
        if (reportGroups.Length == 0)
        {
            writer.WriteLine("No duplicate purchase orders found.");
        }
        writer.WriteLine();
        writer.WriteLine("```json");
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            duplicateRecords = groups.Sum(group => group.Orders.Count),
            duplicateGroups = reportGroups,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        writer.WriteLine("```");
        return writer.ToString();
    }

    public static async Task<string> GenerateAsync(
        PurchaseOrdersApiClient client,
        IEnumerable<Guid> orderIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(orderIds);

        var ids = orderIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            throw new ArgumentException("Select at least one saved draft order.", nameof(orderIds));
        }

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var order = await client.GetPurchaseOrderAsync(id, cancellationToken);
            WriteOrder(writer, order);
        }

        return writer.ToString();
    }

    private static void WriteOrder(TextWriter writer, PurchaseOrderDto order)
    {
        if (!string.Equals(order.Status, "Draft", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Order {order.OrderNumber} is not a draft and cannot be exported as one.");
        }

        var report = new PurchaseOrderReport
        {
            OrderNumber = order.OrderNumber,
            Id = order.Id.ToString("D"),
            SupplierCode = order.SupplierCode,
            SupplierName = order.SupplierName,
            Status = order.Status,
            Notes = order.Notes,
            Total = order.Total,
            CreatedAt = order.CreatedAt,
            Lines = order.Lines.Select(line => new PurchaseOrderReportLine
            {
                LineNumber = line.LineNumber,
                Sku = line.Sku,
                Description = line.Description,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                LineTotal = line.LineTotal,
            }).ToList(),
        };
        WriteReport(writer, report);
    }

    // The demo starts with plain text; the live task replaces this renderer with Markout.
    private static void WriteReport(TextWriter writer, PurchaseOrderReport report)
    {
        writer.WriteLine("Purchase order: {0}", report.OrderNumber);
        writer.WriteLine(report.Notice);
        writer.WriteLine();
        writer.WriteLine("Order ID: {0}", report.Id);
        writer.WriteLine("Supplier code: {0}", report.SupplierCode);
        writer.WriteLine("Supplier name: {0}", report.SupplierName);
        writer.WriteLine("Status: {0}", report.Status);
        writer.WriteLine("Notes: {0}", report.Notes);
        writer.WriteLine("Total: {0}", report.Total);
        writer.WriteLine("Created at: {0:O}", report.CreatedAt);
        writer.WriteLine();
        writer.WriteLine("Line items:");
        foreach (var line in report.Lines)
        {
            writer.WriteLine("Line number: {0}", line.LineNumber);
            writer.WriteLine("SKU: {0}", line.Sku);
            writer.WriteLine("Description: {0}", line.Description);
            writer.WriteLine("Quantity: {0}", line.Quantity);
            writer.WriteLine("Unit price: {0}", line.UnitPrice);
            writer.WriteLine("Line total: {0}", line.LineTotal);
            writer.WriteLine();
        }
        writer.WriteLine("------------------------------------------------------------");
        writer.WriteLine();
    }
}

public sealed class PurchaseOrderReport
{
    public string OrderNumber { get; init; } = "";
    public string Notice => "Saved draft purchase order for review only. This draft has not been sent to suppliers.";
    public string Id { get; init; } = "";
    public string SupplierCode { get; init; } = "";
    public string SupplierName { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Notes { get; init; }
    public decimal Total { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<PurchaseOrderReportLine> Lines { get; init; } = [];
}

public sealed class PurchaseOrderReportLine
{
    public int LineNumber { get; init; }
    public string Sku { get; init; } = "";
    public string Description { get; init; } = "";
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal LineTotal { get; init; }
}
