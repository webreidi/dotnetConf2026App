using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PurchaseOrders.Api.Data;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.IntegrationTests;

[Collection(PurchaseOrdersAppCollection.Name)]
public sealed class SubmissionReplayApiTests(PurchaseOrdersAppFixture fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Missing_or_invalid_identity_is_rejected(string? identity)
    {
        using var api = fixture.CreateApiClient(addSubmissionIdentity: false);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/purchase-orders")
        {
            Content = JsonContent.Create(Request()),
        };
        if (identity is not null)
        {
            message.Headers.Add("Idempotency-Key", identity);
        }

        using var response = await api.SendAsync(message);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_replays_return_the_same_created_response_and_location()
    {
        var identity = Guid.NewGuid();
        var request = Request();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PostAsync(identity, request)));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
            Assert.All(bodies, body => Assert.Equal(bodies[0], body));
            Assert.All(responses, response =>
                Assert.Equal(responses[0].Headers.Location, response.Headers.Location));
            var created = await responses[0].Content.ReadFromJsonAsync<PurchaseOrderDto>();
            Assert.NotNull(created);
            Assert.Equal($"/api/purchase-orders/{created.Id}", responses[0].Headers.Location?.OriginalString);

            await using var db = CreateContext();
            var submission = await db.Submissions.AsNoTracking().SingleAsync(s => s.Id == identity);
            Assert.Equal(created.Id, submission.PurchaseOrderId);
            Assert.Equal(1, await db.PurchaseOrders.CountAsync(o => o.Notes == request.Notes));
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
    public async Task Normalized_replay_preserves_response_and_conflicting_payload_returns_conflict()
    {
        var identity = Guid.NewGuid();
        var request = Request();
        using var original = await PostAsync(identity, request);
        using var replay = await PostAsync(identity, request with
        {
            SupplierCode = " acme ",
            Notes = $" {request.Notes} ",
            Lines = [new(" replay-1 ", " Replay test ", 2, 3.000m)],
        });
        using var conflict = await PostAsync(identity, request with
        {
            Lines = [new("REPLAY-1", "Replay test", 3, 3m)],
        });

        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original.Headers.Location, replay.Headers.Location);
        Assert.Equal(await original.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var db = CreateContext();
        Assert.Equal(1, await db.PurchaseOrders.CountAsync(o => o.Notes == request.Notes));
    }

    [Fact]
    public async Task Identical_payloads_with_different_identities_create_distinct_orders()
    {
        var request = Request();
        using var first = await PostAsync(Guid.NewGuid(), request);
        using var second = await PostAsync(Guid.NewGuid(), request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.NotEqual(first.Headers.Location, second.Headers.Location);
        await using var db = CreateContext();
        Assert.Equal(2, await db.PurchaseOrders.CountAsync(o => o.Notes == request.Notes));
    }

    private static CreatePurchaseOrderRequest Request() => new("ACME", $"Replay {Guid.NewGuid():N}",
        [new("REPLAY-1", "Replay test", 2, 3m)]);

    private async Task<HttpResponseMessage> PostAsync(Guid identity, CreatePurchaseOrderRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/purchase-orders")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", identity.ToString("D"));
        return await fixture.Api.SendAsync(message);
    }

    private PurchasingDbContext CreateContext() => new(
        new DbContextOptionsBuilder<PurchasingDbContext>()
            .UseNpgsql(fixture.DatabaseConnectionString, options => options.EnableRetryOnFailure()).Options);
}
