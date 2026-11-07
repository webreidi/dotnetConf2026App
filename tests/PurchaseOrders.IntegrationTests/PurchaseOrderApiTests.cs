using System.Net.Http.Json;
using System.Text.Json;
using PurchaseOrders.Client;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.IntegrationTests;

[Collection(PurchaseOrdersAppCollection.Name)]
public class PurchaseOrderApiTests(PurchaseOrdersAppFixture fixture)
{
    private readonly HttpClient _api = fixture.Api;

    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        using var response = await _api.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Suppliers_endpoint_returns_only_active_seeded_suppliers()
    {
        var suppliers = await _api.GetFromJsonAsync<List<SupplierDto>>("/api/suppliers");

        Assert.NotNull(suppliers);
        Assert.Contains(suppliers, s => s.Code == "ACME");
        Assert.DoesNotContain(suppliers, s => s.Code == "FABRIKAM");
    }

    [Fact]
    public async Task Creating_a_draft_persists_it_to_the_database()
    {
        var request = new CreatePurchaseOrderRequest("nwind", "Integration test", [
            new("CHAI-24", "Chai tea, case of 24", 4, 18.00m),
            new("SYRUP-12", "Aniseed syrup, case of 12", 2, 10.50m),
        ]);

        using var createResponse = await _api.PostAsJsonAsync("/api/purchase-orders", request);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<PurchaseOrderDto>();
        Assert.NotNull(created);
        Assert.Equal("Draft", created.Status);
        Assert.Equal("NWIND", created.SupplierCode);
        Assert.Equal(93.00m, created.Total);
        Assert.Equal($"/api/purchase-orders/{created.Id}", createResponse.Headers.Location?.OriginalString);

        var fetched = await _api.GetFromJsonAsync<PurchaseOrderDto>($"/api/purchase-orders/{created.Id}");
        Assert.NotNull(fetched);
        Assert.Equal(created.OrderNumber, fetched.OrderNumber);
        Assert.Equal(2, fetched.Lines.Count);
        Assert.Equal("Northwind Traders", fetched.SupplierName);

        var list = await _api.GetFromJsonAsync<List<PurchaseOrderSummaryDto>>("/api/purchase-orders?supplierCode=NWIND");
        Assert.NotNull(list);
        Assert.Contains(list, o => o.Id == created.Id && o.LineCount == 2);
    }

    [Fact]
    public async Task Exporting_saved_drafts_includes_persisted_data_without_changing_orders()
    {
        var createdOrders = new List<PurchaseOrderDto>();
        foreach (var supplier in new[] { "ACME", "NWIND" })
        {
            var request = new CreatePurchaseOrderRequest(supplier, $"Saved notes for {supplier}", [
                new("EXPORT-1", "First saved line", 2, 1.25m),
                new("EXPORT-2", "Second saved line", 3, 2.50m),
            ]);
            using var response = await _api.PostAsJsonAsync("/api/purchase-orders", request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = await response.Content.ReadFromJsonAsync<PurchaseOrderDto>();
            Assert.NotNull(created);
            var saved = await _api.GetFromJsonAsync<PurchaseOrderDto>($"/api/purchase-orders/{created.Id}");
            Assert.NotNull(saved);
            createdOrders.Add(saved);
        }
        var before = await _api.GetFromJsonAsync<List<PurchaseOrderSummaryDto>>("/api/purchase-orders");
        using var exportHttp = fixture.CreateApiClient();
        var client = new PurchaseOrdersApiClient(exportHttp, new PurchaseOrdersClientOptions { MaxAttempts = 1 });

        var report = await PurchaseOrderReportGenerator.GenerateAsync(client, createdOrders.Select(order => order.Id));

        foreach (var order in createdOrders)
        {
            Assert.Contains(order.OrderNumber, report);
            Assert.Contains(order.Id.ToString(), report);
            Assert.Contains(order.SupplierName, report);
            Assert.Contains(order.Notes!, report);
            Assert.Contains("EXPORT-1", report);
            Assert.Contains("First saved line", report);
            Assert.Contains("EXPORT-2", report);
            Assert.Contains("Second saved line", report);
            var after = await client.GetPurchaseOrderAsync(order.Id);
            Assert.Equal(JsonSerializer.Serialize(order), JsonSerializer.Serialize(after));
        }
        Assert.Equal(2, report.Split("has not been sent to suppliers").Length - 1);
        var afterList = await _api.GetFromJsonAsync<List<PurchaseOrderSummaryDto>>("/api/purchase-orders");
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(afterList));
    }

    [Fact]
    public async Task Duplicate_report_finds_all_matching_orders_without_changing_them()
    {
        var notes = $"Duplicate report {Guid.NewGuid():N}";
        var request = new CreatePurchaseOrderRequest("ACME", notes, [
            new("DUP-1", "First duplicate line", 2, 1.25m),
            new("DUP-2", "Second duplicate line", 3, 2.50m),
        ]);
        var savedOrders = new List<PurchaseOrderDto>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await _api.PostAsJsonAsync("/api/purchase-orders", request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var order = await response.Content.ReadFromJsonAsync<PurchaseOrderDto>();
            Assert.NotNull(order);
            var saved = await _api.GetFromJsonAsync<PurchaseOrderDto>($"/api/purchase-orders/{order.Id}");
            Assert.NotNull(saved);
            savedOrders.Add(saved);
        }
        using var uniqueResponse = await _api.PostAsJsonAsync("/api/purchase-orders", request with { Notes = $"{notes} unique" });
        Assert.Equal(HttpStatusCode.Created, uniqueResponse.StatusCode);
        var unique = await uniqueResponse.Content.ReadFromJsonAsync<PurchaseOrderDto>();
        Assert.NotNull(unique);
        for (var i = 0; i < 50; i++)
        {
            using var response = await _api.PostAsJsonAsync("/api/purchase-orders", request with { Notes = $"{notes} filler {i}" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        using var http = fixture.CreateApiClient();
        var client = new PurchaseOrdersApiClient(http, new PurchaseOrdersClientOptions { MaxAttempts = 1 });
        var recent = await client.ListPurchaseOrdersAsync();
        Assert.DoesNotContain(recent, order => savedOrders.Any(saved => saved.Id == order.Id));

        var duplicates = await client.ListDuplicatePurchaseOrdersAsync();
        var report = await PurchaseOrderReportGenerator.GenerateDuplicatesAsync(client);

        Assert.Equal(3, duplicates.Count(order => order.Notes == notes));
        Assert.DoesNotContain(duplicates, order => order.Id == unique.Id);
        Assert.DoesNotContain(unique.Id.ToString(), report);
        foreach (var order in savedOrders)
        {
            Assert.Contains(duplicates, duplicate => duplicate.Id == order.Id);
            Assert.Contains(order.Id.ToString(), report);
            Assert.Contains(order.OrderNumber, report);
            var after = await client.GetPurchaseOrderAsync(order.Id);
            Assert.Equal(JsonSerializer.Serialize(order), JsonSerializer.Serialize(after));
        }
        Assert.Contains("Second duplicate line", report);
    }

    [Fact]
    public async Task Invalid_request_returns_validation_problem()
    {
        var request = new CreatePurchaseOrderRequest("ACME", null, [new("BOLT-10", "Hex bolt", 0, 0.35m)]);

        using var response = await _api.PostAsJsonAsync("/api/purchase-orders", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Lines[0].Quantity", body);
    }

    [Fact]
    public async Task Inactive_supplier_is_rejected()
    {
        var request = new CreatePurchaseOrderRequest("FABRIKAM", null, [new("SCREW-4", "Wood screw", 10, 0.05m)]);

        using var response = await _api.PostAsJsonAsync("/api/purchase-orders", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_order_returns_not_found()
    {
        using var response = await _api.GetAsync($"/api/purchase-orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
