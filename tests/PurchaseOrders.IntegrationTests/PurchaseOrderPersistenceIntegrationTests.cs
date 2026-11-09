using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PurchaseOrders.Api.Data;
using PurchaseOrders.Api.Services;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.IntegrationTests;

[Collection(PurchaseOrdersAppCollection.Name)]
public sealed class PurchaseOrderPersistenceIntegrationTests(PurchaseOrdersAppFixture fixture)
{
    private static readonly JsonSerializerOptions StoredJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Concurrent_database_inserts_resolve_the_unique_identity_violation()
    {
        var interceptor = new ConcurrentSubmissionInsertInterceptor();
        using var logs = new CapturingLoggerProvider();
        await using var database = await PostgresTestDatabase.CreateAsync(
            fixture, commandInterceptor: interceptor, loggerProvider: logs);
        using (var scope = database.CreateScope())
        {
            await PurchasingDatabaseInitializer.InitializeAsync(
                scope.ServiceProvider.GetRequiredService<PurchasingDbContext>());
        }

        var request = new CreatePurchaseOrderRequest("ACME", "Forced concurrent insert", [
            new("RACE-1", "Concurrent line", 2, 1.25m),
        ]);
        var submissionId = Guid.NewGuid();
        using var firstScope = database.CreateScope();
        using var secondScope = database.CreateScope();
        var results = await Task.WhenAll(
            firstScope.ServiceProvider.GetRequiredService<PurchaseOrderService>()
                .CreateDraftAsync(request, submissionId, CancellationToken.None),
            secondScope.ServiceProvider.GetRequiredService<PurchaseOrderService>()
                .CreateDraftAsync(request, submissionId, CancellationToken.None));

        var first = Assert.IsType<CreateDraftResult.Created>(results[0]).Order;
        var second = Assert.IsType<CreateDraftResult.Created>(results[1]).Order;
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(2, interceptor.InsertAttempts);
        Assert.Single(logs.Messages, message =>
            message.Contains("Resolving concurrent replay of submission", StringComparison.Ordinal));
        using var verification = database.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<PurchasingDbContext>();
        Assert.Equal(1, await db.PurchaseOrders.CountAsync());
        Assert.Equal(1, await db.PurchaseOrderSubmissions.CountAsync());
        Assert.Single((await db.PurchaseOrders.Include(order => order.Lines).SingleAsync()).Lines);
    }

    [Fact]
    public async Task Replay_returns_saved_response_after_supplier_is_renamed_and_disabled()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(fixture);
        using (var scope = database.CreateScope())
        {
            await PurchasingDatabaseInitializer.InitializeAsync(
                scope.ServiceProvider.GetRequiredService<PurchasingDbContext>());
        }

        var request = new CreatePurchaseOrderRequest("nwind", "  Saved snapshot  ", [
            new(" saved-1 ", " Saved line ", 2, 3.25m),
        ]);
        var submissionId = Guid.NewGuid();
        PurchaseOrderDto created;
        string savedResponseJson;
        using (var scope = database.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PurchaseOrderService>();
            var result = await service.CreateDraftAsync(request, submissionId, CancellationToken.None);
            created = Assert.IsType<CreateDraftResult.Created>(result).Order;
            var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
            savedResponseJson = await db.PurchaseOrderSubmissions
                .Where(submission => submission.Id == submissionId)
                .Select(submission => submission.ResponseJson!)
                .SingleAsync();
        }

        using (var scope = database.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE suppliers SET "Name" = 'Renamed supplier' , "IsActive" = FALSE WHERE "Code" = 'NWIND'""");
        }

        using (var scope = database.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PurchaseOrderService>();
            var result = await service.CreateDraftAsync(request, submissionId, CancellationToken.None);
            var replayed = Assert.IsType<CreateDraftResult.Created>(result).Order;

            Assert.Equal(created.Id, replayed.Id);
            Assert.Equal("Northwind Traders", replayed.SupplierName);
            Assert.Equal(JsonSerializer.Serialize(replayed, StoredJsonOptions), savedResponseJson);
        }

        using (var scope = database.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
            var supplier = await db.Suppliers.SingleAsync(supplier => supplier.Code == "NWIND");
            Assert.Equal("Renamed supplier", supplier.Name);
            Assert.False(supplier.IsActive);
            Assert.Equal(1, await db.PurchaseOrders.CountAsync());
            Assert.Equal(1, await db.PurchaseOrderSubmissions.CountAsync());
        }
    }

    [Fact]
    public async Task Cancellation_after_save_can_be_replayed_without_creating_another_order()
    {
        var simulator = new ThrowOnceSupplierOrderSimulator(
            () => new OperationCanceledException("Simulated lost response after commit."));
        await using var database = await PostgresTestDatabase.CreateAsync(fixture, simulator);
        using (var scope = database.CreateScope())
        {
            await PurchasingDatabaseInitializer.InitializeAsync(
                scope.ServiceProvider.GetRequiredService<PurchasingDbContext>());
        }

        var request = new CreatePurchaseOrderRequest("ACME", "Cancelled after commit", [
            new("CANCEL-1", "Persisted before cancellation", 1, 7.50m),
        ]);
        var submissionId = Guid.NewGuid();
        using (var scope = database.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PurchaseOrderService>();
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => service.CreateDraftAsync(request, submissionId, CancellationToken.None));
        }

        using (var scope = database.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PurchaseOrderService>();
            var result = await service.CreateDraftAsync(request, submissionId, CancellationToken.None);
            var replayed = Assert.IsType<CreateDraftResult.Created>(result).Order;
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PurchasingDbContext>()
                .PurchaseOrders.CountAsync());
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PurchasingDbContext>()
                .PurchaseOrderSubmissions.CountAsync());
            Assert.Equal(1, simulator.Calls);
            Assert.NotEqual(Guid.Empty, replayed.Id);
        }
    }

    [Fact]
    public async Task Transient_failure_after_save_retries_and_resolves_saved_snapshot()
    {
        var simulator = new ThrowOnceSupplierOrderSimulator(
            () => new NpgsqlException("Simulated transient failure after commit.", new TimeoutException()));
        await using var database = await PostgresTestDatabase.CreateAsync(fixture, simulator);
        using (var scope = database.CreateScope())
        {
            await PurchasingDatabaseInitializer.InitializeAsync(
                scope.ServiceProvider.GetRequiredService<PurchasingDbContext>());
        }

        var request = new CreatePurchaseOrderRequest("ACME", "Transient after commit", [
            new("TRANSIENT-1", "Persisted before retry", 3, 2.10m),
        ]);
        var submissionId = Guid.NewGuid();
        using var serviceScope = database.CreateScope();
        var service = serviceScope.ServiceProvider.GetRequiredService<PurchaseOrderService>();

        var result = await service.CreateDraftAsync(request, submissionId, CancellationToken.None);
        var replayed = Assert.IsType<CreateDraftResult.Created>(result).Order;
        var db = serviceScope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
        var storedResponse = await db.PurchaseOrderSubmissions
            .Where(submission => submission.Id == submissionId)
            .Select(submission => submission.ResponseJson!)
            .SingleAsync();

        Assert.Equal(1, simulator.Calls);
        Assert.Equal(JsonSerializer.Serialize(replayed, StoredJsonOptions), storedResponse);
        Assert.Equal(1, await db.PurchaseOrders.CountAsync());
        Assert.Equal(1, await db.PurchaseOrderSubmissions.CountAsync());
    }
}
