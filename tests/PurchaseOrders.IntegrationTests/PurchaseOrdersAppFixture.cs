using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace PurchaseOrders.IntegrationTests;

public sealed class PurchaseOrdersAppFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    private DistributedApplication? _app;

    public HttpClient Api { get; private set; } = null!;

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

        Api = _app.CreateHttpClient("api", "https");
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

