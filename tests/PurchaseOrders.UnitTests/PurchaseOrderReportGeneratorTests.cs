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
        var first = SampleOrder();
        var second = first with { Id = Guid.NewGuid(), OrderNumber = "PO-COPY", CreatedAt = first.CreatedAt.AddDays(1) };
        var handler = new OrderHandler([first, second], [new([first, second])]);

        var report = await PurchaseOrderReportGenerator.GenerateDuplicatesAsync(CreateClient(handler));

        Assert.StartsWith("# Duplicate purchase orders report", report);
        Assert.Contains("This choice is for review only, not proof", report);
        using var document = JsonDocument.Parse(ExtractJson(report));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("duplicateRecords").GetInt32());
        var group = Assert.Single(root.GetProperty("duplicateGroups").EnumerateArray());
        Assert.Equal(first.Id.ToString("D"), group.GetProperty("canonicalId").GetString());
        var pair = Assert.Single(group.GetProperty("duplicatePairs").EnumerateArray());
        Assert.Equal(second.Id.ToString("D"), pair.GetProperty("duplicateId").GetString());
        Assert.Equal(first.Id.ToString("D"), pair.GetProperty("canonicalId").GetString());
        var reportedOrders = group.GetProperty("orders").EnumerateArray()
            .Select(item => item.GetProperty("order"))
            .ToArray();
        Assert.Equal(2, reportedOrders.Length);
        foreach (var order in new[] { first, second })
        {
            var reported = Assert.Single(reportedOrders, item => item.GetProperty("id").GetGuid() == order.Id);
            Assert.Equal(order.OrderNumber, reported.GetProperty("orderNumber").GetString());
            Assert.Equal(order.SupplierCode, reported.GetProperty("supplierCode").GetString());
            Assert.Equal(order.SupplierName, reported.GetProperty("supplierName").GetString());
            Assert.Equal(order.Status, reported.GetProperty("status").GetString());
            Assert.Equal(order.Notes, reported.GetProperty("notes").GetString());
            Assert.Equal(order.Total, reported.GetProperty("total").GetDecimal());
            Assert.Equal(order.CreatedAt, reported.GetProperty("createdAt").GetDateTimeOffset());
            var lines = reported.GetProperty("lines").EnumerateArray().ToArray();
            Assert.Equal(order.Lines.Count, lines.Length);
            for (var index = 0; index < order.Lines.Count; index++)
            {
                var expectedLine = order.Lines[index];
                var reportedLine = lines[index];
                Assert.Equal(expectedLine.LineNumber, reportedLine.GetProperty("lineNumber").GetInt32());
                Assert.Equal(expectedLine.Sku, reportedLine.GetProperty("sku").GetString());
                Assert.Equal(expectedLine.Description, reportedLine.GetProperty("description").GetString());
                Assert.Equal(expectedLine.Quantity, reportedLine.GetProperty("quantity").GetInt32());
                Assert.Equal(expectedLine.UnitPrice, reportedLine.GetProperty("unitPrice").GetDecimal());
                Assert.Equal(expectedLine.LineTotal, reportedLine.GetProperty("lineTotal").GetDecimal());
            }
        }
        Assert.Equal(["/api/purchase-orders/duplicates/groups"], handler.Paths);
    }

    [Fact]
    public async Task GenerateDuplicatesAsync_breaks_creation_time_ties_by_uuid()
    {
        var createdAt = new DateTimeOffset(2026, 11, 5, 12, 30, 0, TimeSpan.Zero);
        var smallerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var largerId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var first = SampleOrder() with { Id = largerId, CreatedAt = createdAt };
        var second = first with { Id = smallerId, OrderNumber = "PO-SMALLER-ID" };
        var handler = new OrderHandler([first, second], [new([first, second])]);

        var report = await PurchaseOrderReportGenerator.GenerateDuplicatesAsync(CreateClient(handler));

        using var document = JsonDocument.Parse(ExtractJson(report));
        var group = Assert.Single(document.RootElement.GetProperty("duplicateGroups").EnumerateArray());
        Assert.Equal(smallerId.ToString("D"), group.GetProperty("canonicalId").GetString());
        var pair = Assert.Single(group.GetProperty("duplicatePairs").EnumerateArray());
        Assert.Equal(largerId.ToString("D"), pair.GetProperty("duplicateId").GetString());
    }

    [Fact]
    public async Task GenerateDuplicatesAsync_explicitly_reports_no_duplicates()
    {
        var report = await PurchaseOrderReportGenerator.GenerateDuplicatesAsync(CreateClient(new OrderHandler([])));

        using var document = JsonDocument.Parse(ExtractJson(report));
        Assert.Equal(0, document.RootElement.GetProperty("duplicateRecords").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("duplicateGroups").EnumerateArray());
        Assert.Contains("No duplicate purchase orders found.", report);
        Assert.DoesNotContain("\"orderNumber\"", report);
    }

    [Fact]
    public async Task GenerateDuplicatesAsync_honors_cancellation_without_requests()
    {
        var handler = new OrderHandler([]);
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
        var handler = new OrderHandler([order]);

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
        var handler = new OrderHandler([first, second, unselected]);

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
            var report = await PurchaseOrderReportGenerator.GenerateAsync(CreateClient(new OrderHandler([order])), [order.Id]);

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
        var handler = new OrderHandler([order]);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            PurchaseOrderReportGenerator.GenerateAsync(CreateClient(handler), [order.Id, Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task GenerateAsync_rejects_empty_selection_without_requests()
    {
        var handler = new OrderHandler([]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            PurchaseOrderReportGenerator.GenerateAsync(CreateClient(handler), []));

        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task GenerateAsync_rejects_non_drafts_instead_of_labeling_them_as_unsent()
    {
        var order = SampleOrder() with { Status = "Sent" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PurchaseOrderReportGenerator.GenerateAsync(CreateClient(new OrderHandler([order])), [order.Id]));
    }

    [Fact]
    public async Task GenerateAsync_honors_cancellation_without_requests()
    {
        var handler = new OrderHandler([]);
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

    private static string ExtractJson(string report) =>
        report.Split("```json", 2)[1].Split("```", 2)[0];

    private sealed class OrderHandler(PurchaseOrderDto[] orders, DuplicatePurchaseOrderGroupDto[]? duplicateGroups = null) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/api/purchase-orders/duplicates/groups")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(duplicateGroups ?? []),
                });
            }
            if (path == "/api/purchase-orders/duplicates")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(duplicateGroups?.SelectMany(group => group.Orders).ToArray() ?? orders),
                });
            }
            var order = orders.SingleOrDefault(order => path == $"/api/purchase-orders/{order.Id:D}");
            return Task.FromResult(order is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(order) });
        }
    }
}
