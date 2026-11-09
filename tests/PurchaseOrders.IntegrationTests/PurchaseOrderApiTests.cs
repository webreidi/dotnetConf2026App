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
    public async Task Client_timeout_after_confirmed_commit_replays_the_same_identity()
    {
        using var transport = fixture.CreateApiClient();
        using var lostResponse = new LoseCommittedResponseHandler(transport);
        using var http = new HttpClient(lostResponse) { BaseAddress = transport.BaseAddress };
        var client = new PurchaseOrdersApiClient(http, new PurchaseOrdersClientOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(1),
            MaxAttempts = 1,
        });
        var request = new CreatePurchaseOrderRequest("ACME", "HTTP response lost after commit", [
            new("HTTP-TIMEOUT-1", "Committed before timeout", 1, 1.25m),
        ]);
        var submissionId = Guid.NewGuid();

        await Assert.ThrowsAsync<TimeoutException>(() => client.CreateDraftAsync(request, submissionId: submissionId));

        Assert.NotNull(lostResponse.CommittedOrder);
        var committed = await _api.GetFromJsonAsync<PurchaseOrderDto>(
            $"/api/purchase-orders/{lostResponse.CommittedOrder.Id}");
        Assert.NotNull(committed);
        using var replayHttp = fixture.CreateApiClient();
        var replayClient = new PurchaseOrdersApiClient(replayHttp, new PurchaseOrdersClientOptions { MaxAttempts = 1 });
        var replay = await replayClient.CreateDraftAsync(request, submissionId: submissionId);
        Assert.Equal(JsonSerializer.Serialize(lostResponse.CommittedOrder), JsonSerializer.Serialize(replay));
        Assert.Equal(committed.Id, replay.Id);
    }

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

        using var createResponse = await PostDraftAsync(_api, request);

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
            using var response = await PostDraftAsync(_api, request, Guid.NewGuid());
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
            using var response = await PostDraftAsync(_api, request, Guid.NewGuid());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var order = await response.Content.ReadFromJsonAsync<PurchaseOrderDto>();
            Assert.NotNull(order);
            var saved = await _api.GetFromJsonAsync<PurchaseOrderDto>($"/api/purchase-orders/{order.Id}");
            Assert.NotNull(saved);
            savedOrders.Add(saved);
        }
        using var uniqueResponse = await PostDraftAsync(_api, request with { Notes = $"{notes} unique" }, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, uniqueResponse.StatusCode);
        var unique = await uniqueResponse.Content.ReadFromJsonAsync<PurchaseOrderDto>();
        Assert.NotNull(unique);
        for (var i = 0; i < 50; i++)
        {
            using var response = await PostDraftAsync(_api, request with { Notes = $"{notes} filler {i}" }, Guid.NewGuid());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        using var http = fixture.CreateApiClient();
        var client = new PurchaseOrdersApiClient(http, new PurchaseOrdersClientOptions { MaxAttempts = 1 });
        var recent = await client.ListPurchaseOrdersAsync();
        Assert.DoesNotContain(recent, order => savedOrders.Any(saved => saved.Id == order.Id));

        var duplicates = await client.ListDuplicatePurchaseOrdersAsync();
        var groups = await client.ListDuplicatePurchaseOrderGroupsAsync();
        var report = await PurchaseOrderReportGenerator.GenerateDuplicatesAsync(client);

        Assert.Equal(3, duplicates.Count(order => order.Notes == notes));
        var duplicateGroup = Assert.Single(groups, group => group.Orders.Any(order => order.Notes == notes));
        Assert.Equal(3, duplicateGroup.Orders.Count);
        Assert.Equal(
            duplicates.Select(order => order.Id).Order().ToArray(),
            groups.SelectMany(group => group.Orders).Select(order => order.Id).Order().ToArray());
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
        using var reportDocument = JsonDocument.Parse(report.Split("```json", 2)[1].Split("```", 2)[0]);
        var reportGroup = Assert.Single(reportDocument.RootElement.GetProperty("duplicateGroups").EnumerateArray(),
            group => group.GetProperty("orders").EnumerateArray()
                .Any(item => item.GetProperty("order").GetProperty("notes").GetString() == notes));
        Assert.Equal(2, reportGroup.GetProperty("duplicatePairs").GetArrayLength());
    }

    [Fact]
    public async Task Invalid_request_returns_validation_problem()
    {
        var request = new CreatePurchaseOrderRequest("ACME", null, [new("BOLT-10", "Hex bolt", 0, 0.35m)]);

        using var response = await PostDraftAsync(_api, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Lines[0].Quantity", body);
    }

    [Fact]
    public async Task Inactive_supplier_is_rejected()
    {
        var request = new CreatePurchaseOrderRequest("FABRIKAM", null, [new("SCREW-4", "Wood screw", 10, 0.05m)]);

        using var response = await PostDraftAsync(_api, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_order_returns_not_found()
    {
        using var response = await _api.GetAsync($"/api/purchase-orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Missing_or_invalid_idempotency_key_returns_validation_problem()
    {
        var request = new CreatePurchaseOrderRequest("ACME", "Missing key", [
            new("KEY-1", "Key test line", 1, 2.00m),
        ]);
        foreach (var key in new string?[] { null, "not-a-uuid", Guid.Empty.ToString() })
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/purchase-orders")
            {
                Content = JsonContent.Create(request),
            };
            if (key is not null)
            {
                message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            }

            using var response = await _api.SendAsync(message);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Idempotency-Key", body);
        }
    }

    [Fact]
    public async Task Concurrent_posts_with_the_same_identity_create_and_return_one_saved_order()
    {
        var request = new CreatePurchaseOrderRequest("ACME", "Concurrent replay", [
            new("CONCURRENT-1", "One concurrent line", 2, 1.25m),
        ]);
        var submissionId = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => PostDraftAsync(_api, request, submissionId)));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            var orders = await Task.WhenAll(responses.Select(response =>
                response.Content.ReadFromJsonAsync<PurchaseOrderDto>()));
            Assert.All(orders, order => Assert.NotNull(order));
            Assert.All(orders, order => Assert.Equal(orders[0]!.Id, order!.Id));
            Assert.All(orders, order =>
                Assert.Equal(JsonSerializer.Serialize(orders[0]), JsonSerializer.Serialize(order)));

            var saved = await _api.GetFromJsonAsync<PurchaseOrderDto>($"/api/purchase-orders/{orders[0]!.Id}");
            Assert.NotNull(saved);
            Assert.Equal(orders[0]!.Id, saved.Id);
            Assert.Equal(orders[0]!.OrderNumber, saved.OrderNumber);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Reusing_an_identity_with_a_different_payload_returns_conflict()
    {
        var request = new CreatePurchaseOrderRequest("ACME", "Original payload", [
            new("CONFLICT-1", "Original line", 1, 3.00m),
        ]);
        var submissionId = Guid.NewGuid();

        using var first = await PostDraftAsync(_api, request, submissionId);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var conflicting = await PostDraftAsync(_api, request with { Notes = "Changed payload" }, submissionId);

        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
    }

    [Fact]
    public async Task Identical_payloads_with_distinct_identities_create_distinct_orders()
    {
        var request = new CreatePurchaseOrderRequest("ACME", "Separate submissions", [
            new("SEPARATE-1", "Same line", 1, 4.00m),
        ]);

        using var first = await PostDraftAsync(_api, request, Guid.NewGuid());
        using var second = await PostDraftAsync(_api, request, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstOrder = await first.Content.ReadFromJsonAsync<PurchaseOrderDto>();
        var secondOrder = await second.Content.ReadFromJsonAsync<PurchaseOrderDto>();
        Assert.NotNull(firstOrder);
        Assert.NotNull(secondOrder);
        Assert.NotEqual(firstOrder.Id, secondOrder.Id);
    }

    private static Task<HttpResponseMessage> PostDraftAsync(
        HttpClient client,
        CreatePurchaseOrderRequest request,
        Guid? submissionId = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/purchase-orders")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", (submissionId ?? Guid.NewGuid()).ToString());
        return client.SendAsync(message);
    }

    private sealed class LoseCommittedResponseHandler(HttpClient transport) : HttpMessageHandler
    {
        public PurchaseOrderDto? CommittedOrder { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = new ByteArrayContent(await request.Content!.ReadAsByteArrayAsync()),
            };
            foreach (var header in request.Headers)
            {
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            foreach (var header in request.Content.Headers)
            {
                forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Confirm the real API committed before deliberately losing its response.
            using var response = await transport.SendAsync(forwarded, CancellationToken.None);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            CommittedOrder = await response.Content.ReadFromJsonAsync<PurchaseOrderDto>();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The client timeout should cancel the delayed response.");
        }
    }
}
