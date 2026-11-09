using System.Net;
using System.Net.Http.Json;
using PurchaseOrders.Client;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.UnitTests;

public class PurchaseOrdersApiClientTests
{
    private static readonly CreatePurchaseOrderRequest Request =
        new("ACME", null, [new("BOLT-10", "Hex bolt", 10, 0.35m)]);

    private static PurchaseOrderDto SampleOrder() => new(
        Guid.NewGuid(), "PO-20261110-ABC123", "ACME", "Acme Industrial Supply", "Draft", null, 3.50m,
        DateTimeOffset.UtcNow, [new(1, "BOLT-10", "Hex bolt", 10, 0.35m, 3.50m)]);

    private static PurchaseOrdersApiClient CreateClient(StubHandler handler, int maxAttempts = 3) =>
        new(new HttpClient(handler), new PurchaseOrdersClientOptions
        {
            BaseAddress = new Uri("https://po.test/"),
            RequestTimeout = TimeSpan.FromMilliseconds(200),
            MaxAttempts = maxAttempts,
            RetryDelay = TimeSpan.FromMilliseconds(1),
        });

    [Fact]
    public async Task GetSuppliersAsync_returns_suppliers()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[] { new SupplierDto("ACME", "Acme Industrial Supply", "USD") }),
        }));

        var suppliers = await CreateClient(handler).GetSuppliersAsync();

        Assert.Equal("ACME", Assert.Single(suppliers).Code);
        Assert.Equal("/api/suppliers", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetPurchaseOrderAsync_returns_saved_details_using_get()
    {
        var expected = SampleOrder();
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(expected),
        }));

        var order = await CreateClient(handler).GetPurchaseOrderAsync(expected.Id);

        Assert.Equal(expected.Id, order.Id);
        Assert.Equal(expected.SupplierName, order.SupplierName);
        Assert.Equal(expected.Lines, order.Lines);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"/api/purchase-orders/{expected.Id:D}", sent.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetPurchaseOrderAsync_surfaces_missing_order_without_retrying()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(handler).GetPurchaseOrderAsync(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateDraftAsync_posts_request_and_returns_created_order()
    {
        var expected = SampleOrder();
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(expected),
        }));

        var order = await CreateClient(handler).CreateDraftAsync(Request);

        Assert.Equal(expected.OrderNumber, order.OrderNumber);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/api/purchase-orders", sent.RequestUri!.AbsolutePath);
        Assert.NotEqual(Guid.Empty, SubmissionId(sent));
    }

    [Fact]
    public async Task CreateDraftAsync_surfaces_validation_errors_without_retrying()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"title":"One or more validation errors occurred.","errors":{"Lines[0].Quantity":["Quantity must be between 1 and 10000."]}}""",
                System.Text.Encoding.UTF8, "application/problem+json"),
        }));

        var ex = await Assert.ThrowsAsync<PurchaseOrderValidationException>(() => CreateClient(handler).CreateDraftAsync(Request));

        Assert.Contains("Lines[0].Quantity", ex.Errors.Keys);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateDraftAsync_recovers_from_a_transient_timeout()
    {
        var expected = SampleOrder();
        var retries = new List<int>();
        var handler = new StubHandler(async (_, ct) =>
        {
            if (retries.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }

            return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(expected) };
        });
        var client = CreateClient(handler);
        client.Retrying += (_, e) => retries.Add(e.Attempt);

        var order = await client.CreateDraftAsync(Request);

        Assert.Equal(expected.Id, order.Id);
        Assert.Equal([1], retries);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(handler.Requests.Select(SubmissionId).Distinct());
    }

    [Fact]
    public async Task CreateDraftAsync_preserves_identity_and_payload_across_transient_retries()
    {
        var expected = SampleOrder();
        var lines = new List<PurchaseOrderLineRequest> { new("BOLT-10", "Hex bolt", 10, 0.35m) };
        var request = new CreatePurchaseOrderRequest("ACME", null, lines);
        var payloads = new List<string>();
        var handler = new StubHandler(async (message, ct) =>
        {
            payloads.Add(await message.Content!.ReadAsStringAsync(ct));
            if (payloads.Count == 1)
            {
                lines.Clear();
                throw new HttpRequestException("Connection lost after submission.");
            }
            if (payloads.Count == 2)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(expected) };
        });

        var order = await CreateClient(handler).CreateDraftAsync(request);

        Assert.Equal(expected.Id, order.Id);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests.Select(SubmissionId).Distinct());
        Assert.Single(payloads.Distinct());
    }

    [Fact]
    public async Task CreateDraftAsync_explicit_identity_can_replay_after_unknown_outcome_with_a_new_client()
    {
        var submissionId = Guid.NewGuid();
        var expected = SampleOrder();
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout)));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(handler, maxAttempts: 1).CreateDraftAsync(submissionId, Request));

        var replayHandler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(expected),
        }));
        var replay = await CreateClient(replayHandler).CreateDraftAsync(submissionId, Request);

        Assert.Equal(expected.Id, replay.Id);
        Assert.Equal(submissionId, SubmissionId(Assert.Single(handler.Requests)));
        Assert.Equal(submissionId, SubmissionId(Assert.Single(replayHandler.Requests)));
    }

    [Fact]
    public async Task CreateDraftAsync_new_logical_calls_get_distinct_identities_even_for_identical_payloads()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(SampleOrder()),
        }));
        var client = CreateClient(handler);

        await client.CreateDraftAsync(Request);
        await client.CreateDraftAsync(Request, default);

        Assert.Equal(2, handler.Requests.Select(SubmissionId).Distinct().Count());
    }

    [Fact]
    public void CreateDraftAsync_rejects_empty_explicit_identity_without_sending()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Must not send."));

        Assert.Throws<ArgumentException>(() => { _ = CreateClient(handler).CreateDraftAsync(Guid.Empty, Request); });

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateDraftAsync_surfaces_identity_conflict_without_retrying()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(handler).CreateDraftAsync(Guid.NewGuid(), Request));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Single(handler.Requests);
    }

    private static Guid SubmissionId(HttpRequestMessage message) =>
        Guid.ParseExact(Assert.Single(message.Headers.GetValues("Idempotency-Key")), "D");

    [Fact]
    public async Task Requests_throw_TimeoutException_after_max_attempts()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Assert.ThrowsAsync<TimeoutException>(() => CreateClient(handler, maxAttempts: 2).GetSuppliersAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateClient(handler).GetSuppliersAsync(cts.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Options_prefer_aspire_https_endpoint()
    {
        var env = new Dictionary<string, string?>
        {
            ["services__api__https__0"] = "https://localhost:7001",
            ["services__api__http__0"] = "http://localhost:5001",
            ["PURCHASE_ORDERS_API_URL"] = "https://elsewhere",
        };

        var options = PurchaseOrdersClientOptions.FromEnvironment(name => env.GetValueOrDefault(name));

        Assert.Equal(new Uri("https://localhost:7001"), options.BaseAddress);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }
}
