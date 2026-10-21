using System.Net.Http.Json;
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
