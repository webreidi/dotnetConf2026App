using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
if (builder.Configuration.GetValue("Postgres:UseDataVolume", true))
{
    postgres.WithDataVolume("purchase-orders-pgdata");
}

var purchasingDb = postgres.AddDatabase("purchasingdb");

var api = builder.AddProject<Projects.PurchaseOrders_Api>("api")
    .WithReference(purchasingDb)
    .WaitFor(purchasingDb)
    .WithHttpHealthCheck("/health")
    .WithEnvironment("SupplierSimulation__SlowRequestRate", builder.Configuration["SupplierSimulation:SlowRequestRate"] ?? "0.3")
    .WithEnvironment("SupplierSimulation__SlowLatencyMs", builder.Configuration["SupplierSimulation:SlowLatencyMs"] ?? "4000");

if (builder.Configuration.GetValue("DesktopClient:Enabled", true))
{
    builder.AddProject<Projects.PurchaseOrders_WinForms>("purchase-order-desk")
        .WithReference(api)
        .WaitFor(api)
        .ExcludeFromManifest();
}

builder.Build().Run();

