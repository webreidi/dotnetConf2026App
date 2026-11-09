using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace PurchaseOrders.IntegrationTests;

public sealed class PurchaseOrdersAppFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    private DistributedApplication? _app;

    public HttpClient Api { get; private set; } = null!;

    public HttpClient CreateApiClient() => _app!.CreateHttpClient("api", "https");

    public async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken = default) =>
        await _app!.GetConnectionStringAsync("purchasingdb", cancellationToken)
        ?? throw new InvalidOperationException("The purchasing database connection string is unavailable.");

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
}

[CollectionDefinition(Name)]
public sealed class PurchaseOrdersAppCollection : ICollectionFixture<PurchaseOrdersAppFixture>
{
    public const string Name = "PurchaseOrdersApp";
}
