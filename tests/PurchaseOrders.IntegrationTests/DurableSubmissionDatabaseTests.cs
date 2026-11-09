using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PurchaseOrders.Api.Data;
using PurchaseOrders.Api.Domain;
using PurchaseOrders.Api.Services;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.IntegrationTests;

[Collection(PurchaseOrdersAppCollection.Name)]
public sealed class DurableSubmissionDatabaseTests(PurchaseOrdersAppFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_or_cancellation_after_committed_order_replays_original_response_without_preparing_again(
        bool cancelRequest)
    {
        await using var database = await TestDatabase.CreateAsync(fixture.DatabaseConnectionString);
        await using (var setup = database.CreateContext())
        {
            await PurchasingDatabaseInitializer.InitializeAsync(setup);
        }
        var identity = Guid.NewGuid();
        var request = Request();
        using var requestCancellation = new CancellationTokenSource();
        PurchaseOrderDto? committed = null;
        var simulator = new DelegateSimulator(async order =>
        {
            await using var observer = database.CreateContext();
            var submission = await observer.Submissions.AsNoTracking().SingleAsync(s => s.Id == identity);
            Assert.Equal(order.Id, submission.PurchaseOrderId);
            committed = SubmissionSerialization.ReadResponse(submission.ResponseJson!);
            if (cancelRequest)
            {
                requestCancellation.Cancel();
                requestCancellation.Token.ThrowIfCancellationRequested();
            }
            throw new TimeoutException("The response was lost after the database commit.");
        });
        await using (var db = database.CreateContext())
        {
            Task<CreateDraftResult> CreateAsync() =>
                Service(db, simulator).CreateDraftAsync(identity, request, requestCancellation.Token);
            if (cancelRequest)
            {
                var error = await Assert.ThrowsAsync<OperationCanceledException>(CreateAsync);
                Assert.Equal(requestCancellation.Token, error.CancellationToken);
            }
            else
            {
                await Assert.ThrowsAsync<TimeoutException>(CreateAsync);
            }
        }
        Assert.NotNull(committed);

        await using var replayDb = database.CreateContext();
        var replay = Assert.IsType<CreateDraftResult.Created>(
            await Service(replayDb, simulator).CreateDraftAsync(identity, request, CancellationToken.None));

        Assert.Equal(JsonSerializer.Serialize(committed), JsonSerializer.Serialize(replay.Order));
        Assert.Equal(1, simulator.Calls);
        Assert.Equal(1, await replayDb.PurchaseOrders.CountAsync());
        Assert.Equal(1, await replayDb.Submissions.CountAsync());
    }

    [Fact]
    public async Task Provider_retry_after_ambiguous_commit_reads_ledger_instead_of_resaving_tracked_inserts()
    {
        await using var database = await TestDatabase.CreateAsync(fixture.DatabaseConnectionString);
        await using (var setup = database.CreateContext())
        {
            await PurchasingDatabaseInitializer.InitializeAsync(setup);
        }
        var interceptor = new LoseFirstCommitAcknowledgement();
        var simulator = new DelegateSimulator(_ => Task.CompletedTask);
        await using var db = database.CreateContext(interceptor);
        Assert.True(db.Database.CreateExecutionStrategy().RetriesOnFailure);
        var identity = Guid.NewGuid();

        var result = Assert.IsType<CreateDraftResult.Created>(
            await Service(db, simulator).CreateDraftAsync(identity, Request(), CancellationToken.None));

        Assert.Equal(1, interceptor.Commits);
        Assert.Equal(0, simulator.Calls);
        await using var observer = database.CreateContext();
        var submission = await observer.Submissions.AsNoTracking().SingleAsync();
        Assert.Equal(identity, submission.Id);
        Assert.Equal(result.Order.Id, submission.PurchaseOrderId);
        Assert.Equal(JsonSerializer.Serialize(result.Order),
            JsonSerializer.Serialize(SubmissionSerialization.ReadResponse(submission.ResponseJson!)));
        Assert.Equal(1, await observer.PurchaseOrders.CountAsync());
        Assert.Equal(result.Order.Lines.Count, await observer.Set<PurchaseOrderLine>().CountAsync());
    }

    [Fact]
    public async Task Concurrent_fresh_initialization_creates_one_complete_schema()
    {
        await using var database = await TestDatabase.CreateAsync(fixture.DatabaseConnectionString);
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();

        await Task.WhenAll(
            PurchasingDatabaseInitializer.InitializeAsync(first),
            PurchasingDatabaseInitializer.InitializeAsync(second));
        await PurchasingDatabaseInitializer.InitializeAsync(first);

        Assert.Equal(4, await first.Suppliers.CountAsync());
        Assert.Equal(0, await first.PurchaseOrders.CountAsync());
        Assert.Equal(0, await first.Submissions.CountAsync());
        Assert.True(await HasColumnAsync(first, "ResponseJson"));
    }

    [Fact]
    public async Task Retained_full_schema_preserves_seven_identities_payloads_and_exact_historical_responses()
    {
        await using var database = await TestDatabase.CreateAsync(fixture.DatabaseConnectionString);
        var retained = await SeedRetainedAsync(database, withResponses: true, count: 7);
        await using var db = database.CreateContext();
        var before = await SnapshotAsync(db);

        await PurchasingDatabaseInitializer.InitializeAsync(db);
        await PurchasingDatabaseInitializer.InitializeAsync(db);

        Assert.Equal(before, await SnapshotAsync(db));
        foreach (var row in retained)
        {
            var saved = await db.Submissions.AsNoTracking().SingleAsync(s => s.Id == row.Identity);
            Assert.Equal(row.Payload, saved.RequestPayload);
            Assert.Equal(row.Response, saved.ResponseJson);
            var result = Assert.IsType<CreateDraftResult.Created>(await Service(db).CreateDraftAsync(
                row.Identity, row.Request, CancellationToken.None));
            Assert.Equal(JsonSerializer.Serialize(SubmissionSerialization.ReadResponse(row.Response!)),
                JsonSerializer.Serialize(result.Order));
            Assert.Equal(row.OrderId, result.Order.Id);
        }
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task Legacy_ledger_without_response_column_is_backfilled_once_without_rewriting_payload_or_ids()
    {
        await using var database = await TestDatabase.CreateAsync(fixture.DatabaseConnectionString);
        var retained = Assert.Single(await SeedRetainedAsync(database, withResponses: false, count: 1));
        await using var db = database.CreateContext();
        var before = await SnapshotAsync(db);
        Assert.False(await HasColumnAsync(db, "ResponseJson"));

        await PurchasingDatabaseInitializer.InitializeAsync(db);
        var saved = await db.Submissions.AsNoTracking().SingleAsync();
        Assert.Equal(retained.Payload, saved.RequestPayload);
        Assert.Equal(retained.Identity, saved.Id);
        Assert.Equal(retained.OrderId, saved.PurchaseOrderId);
        var backfilledResponse = saved.ResponseJson;
        Assert.False(string.IsNullOrWhiteSpace(backfilledResponse));
        Assert.Equal(before, await SnapshotAsync(db));

        await db.Suppliers.Where(s => s.Code == "ACME").ExecuteUpdateAsync(
            updates => updates.SetProperty(s => s.Name, "Renamed after backfill"));
        await PurchasingDatabaseInitializer.InitializeAsync(db);
        var replay = Assert.IsType<CreateDraftResult.Created>(
            await Service(db).CreateDraftAsync(retained.Identity, retained.Request, CancellationToken.None));

        Assert.Equal(JsonSerializer.Serialize(SubmissionSerialization.ReadResponse(backfilledResponse!)),
            JsonSerializer.Serialize(replay.Order));
        Assert.Equal(backfilledResponse, (await db.Submissions.AsNoTracking().SingleAsync()).ResponseJson);
        Assert.Equal(retained.Payload, (await db.Submissions.AsNoTracking().SingleAsync()).RequestPayload);
        Assert.Equal(before, await SnapshotAsync(db));
        Assert.False(await db.Database.SqlQueryRaw<bool>("""
            SELECT is_nullable = 'YES' AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'purchase_order_submissions' AND column_name = 'ResponseJson'
            """).SingleAsync());
    }

    [Fact]
    public async Task Failed_unique_index_upgrade_rolls_back_column_indexes_and_preserves_every_original_row()
    {
        await using var database = await TestDatabase.CreateAsync(fixture.DatabaseConnectionString);
        var retained = Assert.Single(await SeedRetainedAsync(database, withResponses: false, count: 1));
        await using var db = database.CreateContext();
        var duplicateIdentity = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO purchase_order_submissions ("Id", "RequestPayload", "PurchaseOrderId")
            VALUES ({duplicateIdentity}, {retained.Payload}, {retained.OrderId})
            """);
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<PostgresException>(() => PurchasingDatabaseInitializer.InitializeAsync(db));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.False(await HasColumnAsync(db, "ResponseJson"));
        Assert.Equal(before, await SnapshotAsync(db));
        Assert.False(await db.Database.SqlQueryRaw<bool>("""
            SELECT to_regclass('public."IX_purchase_order_submissions_PurchaseOrderId"') IS NOT NULL AS "Value"
            """).SingleAsync());
        var payloads = await db.Database.SqlQueryRaw<string>("""
            SELECT "RequestPayload" AS "Value" FROM purchase_order_submissions
            """).ToListAsync();
        Assert.Equal(2, payloads.Count);
        Assert.All(payloads, payload => Assert.Equal(retained.Payload, payload));
        var identities = await db.Database.SqlQueryRaw<Guid>("""
            SELECT "Id" AS "Value" FROM purchase_order_submissions
            """).ToListAsync();
        Assert.Contains(retained.Identity, identities);
        Assert.Contains(duplicateIdentity, identities);
    }

    private static CreatePurchaseOrderRequest Request() => new("ACME", null,
        [new("RETAIN-1", "Retained line", 2, 3m)]);

    private static PurchaseOrderService Service(PurchasingDbContext db, ISupplierOrderSimulator? simulator = null) =>
        new(db, simulator ?? new DelegateSimulator(_ => throw new InvalidOperationException("Replay prepared again.")),
            TimeProvider.System, NullLogger<PurchaseOrderService>.Instance);

    private static Task<bool> HasColumnAsync(PurchasingDbContext db, string name) =>
        db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'purchase_order_submissions'
                AND column_name = {name}) AS "Value"
            """).SingleAsync();

    private static async Task<string> SnapshotAsync(PurchasingDbContext db)
    {
        var orders = await db.PurchaseOrders.AsNoTracking().OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.OrderNumber, o.SupplierCode, o.Status, o.Notes, o.CreatedAt }).ToListAsync();
        var lines = await db.Set<PurchaseOrderLine>().AsNoTracking().OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.PurchaseOrderId, l.LineNumber, l.Sku, l.Description, l.Quantity, l.UnitPrice }).ToListAsync();
        var submissions = await db.Database.SqlQueryRaw<Guid>("""
            SELECT "Id" AS "Value" FROM purchase_order_submissions ORDER BY "Id"
            """).ToListAsync();
        return JsonSerializer.Serialize(new { orders, lines, submissions });
    }

    private static async Task<List<RetainedSubmission>> SeedRetainedAsync(TestDatabase database, bool withResponses, int count)
    {
        await using var db = database.CreateContext();
        // Freeze the observed pre-change schema rather than deriving compatibility fixtures from the current EF model.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE suppliers (
                "Code" character varying(16) NOT NULL,
                "Name" character varying(200) NOT NULL,
                "Currency" character(3) NOT NULL,
                "IsActive" boolean NOT NULL,
                CONSTRAINT "PK_suppliers" PRIMARY KEY ("Code")
            );
            CREATE TABLE purchase_orders (
                "Id" uuid NOT NULL,
                "OrderNumber" character varying(32) NOT NULL,
                "SupplierCode" character varying(16) NOT NULL,
                "Status" character varying(16) NOT NULL,
                "Notes" character varying(500),
                "CreatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_purchase_orders" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_purchase_orders_suppliers_SupplierCode"
                    FOREIGN KEY ("SupplierCode") REFERENCES suppliers ("Code") ON DELETE RESTRICT
            );
            CREATE TABLE purchase_order_lines (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                "PurchaseOrderId" uuid NOT NULL,
                "LineNumber" integer NOT NULL,
                "Sku" character varying(32) NOT NULL,
                "Description" character varying(200) NOT NULL,
                "Quantity" integer NOT NULL,
                "UnitPrice" numeric(18,2) NOT NULL,
                CONSTRAINT "PK_purchase_order_lines" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_purchase_order_lines_purchase_orders_PurchaseOrderId"
                    FOREIGN KEY ("PurchaseOrderId") REFERENCES purchase_orders ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX "IX_purchase_orders_OrderNumber" ON purchase_orders ("OrderNumber");
            CREATE INDEX "IX_purchase_orders_SupplierCode" ON purchase_orders ("SupplierCode");
            CREATE UNIQUE INDEX "IX_purchase_order_lines_PurchaseOrderId_LineNumber"
                ON purchase_order_lines ("PurchaseOrderId", "LineNumber");
            INSERT INTO suppliers ("Code", "Name", "Currency", "IsActive") VALUES
                ('ACME', 'Acme Industrial Supply', 'USD', true),
                ('NWIND', 'Northwind Traders', 'USD', true),
                ('CONTOSO', 'Contoso Components', 'EUR', true),
                ('FABRIKAM', 'Fabrikam Fasteners', 'USD', false);
            CREATE TABLE purchase_order_submissions (
                "Id" uuid NOT NULL CONSTRAINT pk_purchase_order_submissions PRIMARY KEY,
                "RequestPayload" text NOT NULL,
                "PurchaseOrderId" uuid NOT NULL,
                CONSTRAINT purchase_order_submissions_purchase_order_id_fkey
                    FOREIGN KEY ("PurchaseOrderId") REFERENCES purchase_orders ("Id") ON DELETE RESTRICT
            )
            """);
        if (withResponses)
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE purchase_order_submissions ADD COLUMN "ResponseJson" text NOT NULL;
                CREATE UNIQUE INDEX "IX_purchase_order_submissions_PurchaseOrderId"
                    ON purchase_order_submissions ("PurchaseOrderId")
                """);
        }
        var supplier = await db.Suppliers.SingleAsync(s => s.Code == "ACME");
        var retained = new List<RetainedSubmission>();
        for (var i = 0; i < count; i++)
        {
            var request = Request() with { Notes = i % 2 == 0 ? "" : null };
            var order = PurchaseOrder.CreateDraft(supplier, $"RETAINED-{i}", request.Notes,
                request.Lines.Select(l => new NewPurchaseOrderLine(l.Sku, l.Description, l.Quantity, l.UnitPrice)),
                new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(i + 1));
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync();
            var identity = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(new
            {
                request.SupplierCode,
                request.Notes,
                Lines = request.Lines.Select(l => new
                {
                    l.Sku, l.Description, l.Quantity, UnitPrice = "3.00",
                }),
            });
            var historical = order.ToDto() with { SupplierName = "Original supplier name before rename" };
            var response = JsonSerializer.Serialize(historical, i % 2 == 0
                ? new JsonSerializerOptions() : new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (withResponses)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO purchase_order_submissions ("Id", "RequestPayload", "PurchaseOrderId", "ResponseJson")
                    VALUES ({identity}, {payload}, {order.Id}, {response})
                    """);
            }
            else
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO purchase_order_submissions ("Id", "RequestPayload", "PurchaseOrderId")
                    VALUES ({identity}, {payload}, {order.Id})
                    """);
            }
            retained.Add(new(identity, order.Id, request, payload, withResponses ? response : null));
        }
        return retained;
    }

    private sealed record RetainedSubmission(
        Guid Identity, Guid OrderId, CreatePurchaseOrderRequest Request, string Payload, string? Response);

    private sealed class DelegateSimulator(Func<PurchaseOrder, Task> prepare) : ISupplierOrderSimulator
    {
        public int Calls { get; private set; }

        public Task PrepareDraftSubmissionAsync(PurchaseOrder order, CancellationToken cancellationToken)
        {
            Calls++;
            return prepare(order);
        }
    }

    private sealed class LoseFirstCommitAcknowledgement : DbTransactionInterceptor
    {
        public int Commits { get; private set; }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Commits++;
            if (Commits == 1)
            {
                throw new NpgsqlException("Lost commit acknowledgement.", new TimeoutException());
            }
            return Task.CompletedTask;
        }
    }

    private sealed class TestDatabase(string serverConnectionString, string connectionString, string name) : IAsyncDisposable
    {
        public static async Task<TestDatabase> CreateAsync(string serverConnectionString)
        {
            var name = $"replay_test_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(serverConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"""CREATE DATABASE "{name}" """, connection);
            await command.ExecuteNonQueryAsync();
            var builder = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = name, Pooling = false };
            return new(serverConnectionString, builder.ConnectionString, name);
        }

        public PurchasingDbContext CreateContext(params IInterceptor[] interceptors)
        {
            var options = new DbContextOptionsBuilder<PurchasingDbContext>()
                .UseNpgsql(connectionString, provider => provider.EnableRetryOnFailure());
            options.AddInterceptors(interceptors);
            return new(options.Options);
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(serverConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"""DROP DATABASE "{name}" WITH (FORCE) """, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
