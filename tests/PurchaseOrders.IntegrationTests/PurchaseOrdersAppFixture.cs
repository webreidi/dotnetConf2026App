using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace PurchaseOrders.IntegrationTests;

public sealed class PurchaseOrdersAppFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    private DistributedApplication? _app;

    public HttpClient Api { get; private set; } = null!;

    public string DatabaseConnectionString { get; private set; } = null!;

    public HttpClient CreateApiClient(bool addSubmissionIdentity = true)
    {
        var client = _app!.CreateHttpClient("api", "https");
        return addSubmissionIdentity
            ? new HttpClient(new SubmissionIdentityHandler(client)) { BaseAddress = client.BaseAddress }
            : client;
    }

    public async Task InitializeAsync()
    {
        using var cts = new CancellationTokenSource(StartupTimeout);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.PurchaseOrders_AppHost>(
            [
                "DesktopClient:Enabled=false",
                "Postgres:UseDataVolume=false",
                "SupplierSimulation:SlowRequestRate=0",
            ],
            cts.Token);

        appHost.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddFilter("Aspire.", LogLevel.Warning);
        });

        _app = await appHost.BuildAsync(cts.Token);
        await _app.StartAsync(cts.Token);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("api", cts.Token);

        DatabaseConnectionString = await _app.GetConnectionStringAsync("purchasingdb", cts.Token)
            ?? throw new InvalidOperationException("The test database connection string is unavailable.");
        Api = CreateApiClient();
    }

    public async Task DisposeAsync()
    {
        Api?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private sealed class SubmissionIdentityHandler(HttpClient client) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == "/api/purchase-orders" &&
                !request.Headers.Contains("Idempotency-Key"))
            {
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            }

            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = request.Content,
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
            };
            foreach (var header in request.Headers)
            {
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return await client.SendAsync(forwarded, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PurchaseOrdersAppCollection : ICollectionFixture<PurchaseOrdersAppFixture>
{
    public const string Name = "PurchaseOrdersApp";
}
