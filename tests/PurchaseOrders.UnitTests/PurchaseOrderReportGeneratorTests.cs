using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PurchaseOrders.Client;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.UnitTests;

public class PurchaseOrderReportGeneratorTests
{
    [Fact]
    public async Task GenerateDuplicatesAsync_exports_all_returned_duplicates_with_full_details()
    {
        var first = SampleOrder() with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
        };
        var second = first with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            OrderNumber = "PO-COPY",
        };
        var handler = new OrderHandler(first, second);

        var report = await PurchaseOrderReportGenerator.GenerateDuplicatesAsync(CreateClient(handler));

        Assert.Contains("# Duplicate purchase orders", report);
        Assert.Contains("for review only, not proof of the original", report);
        Assert.Contains("Duplicate records: 2", report);
        using var json = ReadReportJson(report);
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("duplicateRecordCount").GetInt32());
        var group = Assert.Single(root.GetProperty("groups").EnumerateArray());
        var canonicalId = second.Id;
        Assert.Equal(canonicalId, group.GetProperty("canonicalId").GetGuid());
        var records = group.GetProperty("records").EnumerateArray().ToArray();
        Assert.Equal(2, records.Length);
        foreach (var (record, expectedOrder) in records.Zip(new[] { first, second }))
        {
            Assert.Equal(expectedOrder.Id, record.GetProperty("duplicateId").GetGuid());
            Assert.Equal(canonicalId, record.GetProperty("canonicalId").GetGuid());
            var order = record.GetProperty("order");
            Assert.Equal(expectedOrder.Id, order.GetProperty("id").GetGuid());
            Assert.Equal(expectedOrder.OrderNumber, order.GetProperty("orderNumber").GetString());
            Assert.Equal(expectedOrder.SupplierCode, order.GetProperty("supplierCode").GetString());
            Assert.Equal(expectedOrder.SupplierName, order.GetProperty("supplierName").GetString());
            Assert.Equal(expectedOrder.Status, order.GetProperty("status").GetString());
            Assert.Equal(expectedOrder.Notes, order.GetProperty("notes").GetString());
            Assert.Equal(expectedOrder.Total, order.GetProperty("total").GetDecimal());
            Assert.Equal(expectedOrder.CreatedAt, order.GetProperty("createdAt").GetDateTimeOffset());
            var lines = order.GetProperty("lines").EnumerateArray().ToArray();
            Assert.Equal(expectedOrder.Lines.Count, lines.Length);
            foreach (var (line, expectedLine) in lines.Zip(expectedOrder.Lines))
            {
                Assert.Equal(expectedLine.LineNumber, line.GetProperty("lineNumber").GetInt32());
                Assert.Equal(expectedLine.Sku, line.GetProperty("sku").GetString());
                Assert.Equal(expectedLine.Description, line.GetProperty("description").GetString());
                Assert.Equal(expectedLine.Quantity, line.GetProperty("quantity").GetInt32());
                Assert.Equal(expectedLine.UnitPrice, line.GetProperty("unitPrice").GetDecimal());
                Assert.Equal(expectedLine.LineTotal, line.GetProperty("lineTotal").GetDecimal());
            }
        }
        Assert.Equal(["/api/purchase-orders/duplicates/groups"], handler.Paths);
    }

    [Fact]
    public async Task GenerateDuplicatesAsync_explicitly_reports_no_duplicates()
    {
        var report = await PurchaseOrderReportGenerator.GenerateDuplicatesAsync(CreateClient(new OrderHandler()));

        Assert.Contains("Duplicate records: 0", report);
        Assert.Contains("No duplicate purchase orders found.", report);
        using var json = ReadReportJson(report);
        Assert.Empty(json.RootElement.GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public async Task GenerateDuplicatesAsync_honors_cancellation_without_requests()
    {
        var handler = new OrderHandler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PurchaseOrderReportGenerator.GenerateDuplicatesAsync(CreateClient(handler), cts.Token));

        Assert.Empty(handler.Paths);
    }

    private static PurchaseOrderDto SampleOrder(string number = "PO-SAVED-001") => new(
        Guid.NewGuid(), number, "ACME", "Acme Industrial Supply", "Draft", "Deliver to warehouse",
        19.875m, new DateTimeOffset(2026, 11, 5, 12, 30, 0, TimeSpan.FromHours(-8)),
        [new(1, "BOLT-10", "Hex bolt", 10, 0.35m, 3.50m),
         new(2, "NUT-20", "Lock nut", 5, 3.275m, 16.375m)]);

    [Fact]
    public async Task GenerateAsync_includes_complete_saved_details_and_all_lines()
    {
        var order = SampleOrder();
        var handler = new OrderHandler(order);

        var report = await PurchaseOrderReportGenerator.GenerateAsync(CreateClient(handler), [order.Id]);

        Assert.Contains(order.OrderNumber, report);
        Assert.Contains("has not been sent to suppliers", report);
        Assert.Contains(order.Id.ToString(), report);
        Assert.Contains(order.SupplierCode, report);
        Assert.Contains(order.SupplierName, report);
        Assert.Contains(order.Status, report);
        Assert.Contains(order.Notes!, report);
        Assert.Contains("19.875", report);
        Assert.Contains("2026-11-05T12:30:00.0000000-08:00", report);
        foreach (var line in order.Lines)
        {
            Assert.Contains($"Line number: {line.LineNumber}", report);
            Assert.Contains(line.Sku, report);
            Assert.Contains(line.Description, report);
            Assert.Contains($"Quantity: {line.Quantity}", report);
            Assert.Contains(line.UnitPrice.ToString(CultureInfo.InvariantCulture), report);
            Assert.Contains(line.LineTotal.ToString(CultureInfo.InvariantCulture), report);
        }
        Assert.Equal([$"/api/purchase-orders/{order.Id:D}"], handler.Paths);
    }

    [Fact]
    public async Task GenerateAsync_combines_only_selected_orders_in_selection_order_without_duplicates()
    {
        var first = SampleOrder("PO-FIRST");
        var second = SampleOrder("PO-SECOND");
        var unselected = SampleOrder("PO-UNSELECTED");
        var handler = new OrderHandler(first, second, unselected);

        var report = await PurchaseOrderReportGenerator.GenerateAsync(CreateClient(handler), [second.Id, first.Id, second.Id]);

        Assert.True(report.IndexOf(second.OrderNumber, StringComparison.Ordinal) < report.IndexOf(first.OrderNumber, StringComparison.Ordinal));
        Assert.DoesNotContain(unselected.OrderNumber, report);
        Assert.Equal(2, report.Split("has not been sent to suppliers").Length - 1);
        Assert.Equal(2, report.Split("------------------------------------------------------------").Length - 1);
        Assert.Equal([$"/api/purchase-orders/{second.Id:D}", $"/api/purchase-orders/{first.Id:D}"], handler.Paths);
    }

    [Fact]
    public async Task GenerateAsync_preserves_decimal_values_under_non_english_culture_and_literal_text()
    {
        var order = SampleOrder() with
        {
            Notes = null,
            Lines = [new(1, "SKU|SPECIAL", "Nut | bolt", 2, 1.2345m, 2.469m)],
        };
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var report = await PurchaseOrderReportGenerator.GenerateAsync(CreateClient(new OrderHandler(order)), [order.Id]);

            Assert.Contains("1.2345", report);
            Assert.Contains("2.469", report);
            Assert.Contains("SKU|SPECIAL", report);
            Assert.Contains("Nut | bolt", report);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task GenerateAsync_fails_instead_of_returning_a_partial_document_when_an_order_is_missing()
    {
        var order = SampleOrder();
        var handler = new OrderHandler(order);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            PurchaseOrderReportGenerator.GenerateAsync(CreateClient(handler), [order.Id, Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task GenerateAsync_rejects_empty_selection_without_requests()
    {
        var handler = new OrderHandler();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            PurchaseOrderReportGenerator.GenerateAsync(CreateClient(handler), []));

        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task GenerateAsync_rejects_non_drafts_instead_of_labeling_them_as_unsent()
    {
        var order = SampleOrder() with { Status = "Sent" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PurchaseOrderReportGenerator.GenerateAsync(CreateClient(new OrderHandler(order)), [order.Id]));
    }

    [Fact]
    public async Task GenerateAsync_honors_cancellation_without_requests()
    {
        var handler = new OrderHandler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PurchaseOrderReportGenerator.GenerateAsync(CreateClient(handler), [Guid.NewGuid()], cts.Token));

        Assert.Empty(handler.Paths);
    }

    private static PurchaseOrdersApiClient CreateClient(OrderHandler handler) =>
        new(new HttpClient(handler), new PurchaseOrdersClientOptions
        {
            BaseAddress = new Uri("https://po.test/"),
            MaxAttempts = 1,
        });

    private static JsonDocument ReadReportJson(string report)
    {
        var start = report.IndexOf("```json", StringComparison.Ordinal);
        var end = report.IndexOf("```", start + "```json".Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The report must contain a fenced JSON document.");
        return JsonDocument.Parse(report[(start + "```json".Length)..end].Trim());
    }

    private sealed class OrderHandler(params PurchaseOrderDto[] orders) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/api/purchase-orders/duplicates/groups")
            {
                var groups = orders.Length > 1 ? new[] { new PurchaseOrderDuplicateGroupDto(orders) } : [];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(groups) });
            }
            var order = orders.SingleOrDefault(order => path == $"/api/purchase-orders/{order.Id:D}");
            return Task.FromResult(order is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(order) });
        }
    }
}
