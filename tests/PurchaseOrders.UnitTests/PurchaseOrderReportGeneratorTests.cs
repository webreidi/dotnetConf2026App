using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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
        var handler = new OrderHandler(first, second);

        var report = await PurchaseOrderReportGenerator.GenerateDuplicatesAsync(CreateClient(handler));

        Assert.Contains("Duplicate records: 2", report);
        foreach (var order in new[] { first, second })
        {
            Assert.Contains($"Order ID: {order.Id:D}", report);
            Assert.Contains($"Purchase order: {order.OrderNumber}", report);
            Assert.Contains($"Created at: {order.CreatedAt:O}", report);
        }
        Assert.Equal(2, report.Split("Line number: 2").Length - 1);
        Assert.Contains(first.SupplierName, report);
        Assert.Contains(first.Notes!, report);
        Assert.Contains("NUT-20", report);
        Assert.Equal(["/api/purchase-orders/duplicates"], handler.Paths);
    }

    [Fact]
    public async Task GenerateDuplicatesAsync_explicitly_reports_no_duplicates()
    {
        var report = await PurchaseOrderReportGenerator.GenerateDuplicatesAsync(CreateClient(new OrderHandler()));

        Assert.Contains("Duplicate records: 0", report);
        Assert.Contains("No duplicate purchase orders found.", report);
        Assert.DoesNotContain("Purchase order:", report);
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

    private sealed class OrderHandler(params PurchaseOrderDto[] orders) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/api/purchase-orders/duplicates")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(orders) });
            }
            var order = orders.SingleOrDefault(order => path == $"/api/purchase-orders/{order.Id:D}");
            return Task.FromResult(order is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(order) });
        }
    }
}
