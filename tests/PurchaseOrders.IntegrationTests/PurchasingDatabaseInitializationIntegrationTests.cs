using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PurchaseOrders.Api.Data;
using PurchaseOrders.Api.Services;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.IntegrationTests;

[Collection(PurchaseOrdersAppCollection.Name)]
public sealed class PurchasingDatabaseInitializationIntegrationTests(PurchaseOrdersAppFixture fixture)
{
    private static readonly Guid HistoricalSubmissionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid HistoricalOrderId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Repeated_initialization_preserves_populated_legacy_schema_rows_and_replays_history()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(fixture);
        await ExecuteSqlAsync(database.ConnectionString, LegacyDatabaseScript);

        var before = await ReadSnapshotAsync(database.ConnectionString);
        Assert.Equal(41, before.OrderCount);
        Assert.Equal(42, before.LineCount);
        Assert.Equal(5, before.Submissions.Count);
        Assert.False(before.ResponseJsonNullable);

        using (var scope = database.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
            await PurchasingDatabaseInitializer.InitializeAsync(db);
            await PurchasingDatabaseInitializer.InitializeAsync(db);
        }

        var after = await ReadSnapshotAsync(database.ConnectionString);
        Assert.Equal(before.OrderCount, after.OrderCount);
        Assert.Equal(before.LineCount, after.LineCount);
        Assert.Equal(before.Submissions, after.Submissions);
        Assert.Equal(before.OrdersJson, after.OrdersJson);
        Assert.Equal(before.LinesJson, after.LinesJson);
        Assert.False(after.ResponseJsonNullable);

        var historicalRequest = new CreatePurchaseOrderRequest("NWIND", "historic note", [
            new("CHAI-24", "Chai tea, case of 24", 4, 18.00m),
            new("MILK-2", "Second legacy line", 2, 1.50m),
        ]);
        using (var scope = database.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PurchaseOrderService>();
            var result = await service.CreateDraftAsync(
                historicalRequest, HistoricalSubmissionId, CancellationToken.None);
            var replayed = Assert.IsType<CreateDraftResult.Created>(result).Order;

            Assert.Equal(HistoricalOrderId, replayed.Id);
            Assert.Equal("Northwind Traders", replayed.SupplierName);
            Assert.Equal("PO-LEGACY-001", replayed.OrderNumber);
            Assert.Equal("historic note", replayed.Notes);
            Assert.Equal(["CHAI-24", "MILK-2"], replayed.Lines.Select(line => line.Sku));
            Assert.Equal(18.00m, replayed.Lines[0].UnitPrice);
            Assert.Equal(
                before.Submissions.Single(submission => submission.Id == HistoricalSubmissionId).ResponseJson,
                JsonSerializer.Serialize(replayed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var reordered = historicalRequest with
            {
                Lines = historicalRequest.Lines.Reverse().ToArray(),
            };
            Assert.IsType<CreateDraftResult.Conflict>(
                await service.CreateDraftAsync(reordered, HistoricalSubmissionId, CancellationToken.None));
        }

        using (var scope = database.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PurchaseOrderService>();
            var nullNotesRequest = new CreatePurchaseOrderRequest("NWIND", null, [
                new("LEGACY-PO-LEGACY-002", "Legacy item", 1, 1.00m),
            ]);
            var result = await service.CreateDraftAsync(
                nullNotesRequest, Guid.Parse("20000000-0000-0000-0000-000000000002"), CancellationToken.None);
            var replayed = Assert.IsType<CreateDraftResult.Created>(result).Order;
            Assert.Null(replayed.Notes);
            Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000002"), replayed.Id);
        }

        var afterReplay = await ReadSnapshotAsync(database.ConnectionString);
        Assert.Equal(after.Submissions, afterReplay.Submissions);
        Assert.Equal(41, afterReplay.OrderCount);
        Assert.Equal(42, afterReplay.LineCount);
        Assert.Equal(before.OrdersJson, afterReplay.OrdersJson);
        Assert.Equal(before.LinesJson, afterReplay.LinesJson);
    }

    [Fact]
    public async Task Legacy_ledger_without_response_column_is_upgraded_and_first_replay_freezes_snapshot()
    {
        using var logs = new CapturingLoggerProvider();
        await using var database = await PostgresTestDatabase.CreateAsync(fixture, loggerProvider: logs);
        await ExecuteSqlAsync(database.ConnectionString, LegacyDatabaseScript);
        var original = await ReadSnapshotAsync(database.ConnectionString);
        await ExecuteSqlAsync(database.ConnectionString,
            """ALTER TABLE purchase_order_submissions DROP COLUMN "ResponseJson" """);

        using (var scope = database.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
            await PurchasingDatabaseInitializer.InitializeAsync(db);
            await PurchasingDatabaseInitializer.InitializeAsync(db);
        }

        var upgraded = await ReadSnapshotAsync(database.ConnectionString);
        Assert.True(upgraded.ResponseJsonNullable);
        Assert.Equal(original.OrdersJson, upgraded.OrdersJson);
        Assert.Equal(original.LinesJson, upgraded.LinesJson);
        Assert.Equal(
            original.Submissions.Select(row => (row.Id, row.RequestPayload, row.PurchaseOrderId)),
            upgraded.Submissions.Select(row => (row.Id, row.RequestPayload, row.PurchaseOrderId)));
        Assert.All(upgraded.Submissions, row => Assert.Null(row.ResponseJson));

        var request = new CreatePurchaseOrderRequest("NWIND", "historic note", [
            new("CHAI-24", "Chai tea, case of 24", 4, 18m),
            new("MILK-2", "Second legacy line", 2, 1.5m),
        ]);
        PurchaseOrderDto firstReplay;
        using (var scope = database.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PurchaseOrderService>();
            firstReplay = Assert.IsType<CreateDraftResult.Created>(
                await service.CreateDraftAsync(request, HistoricalSubmissionId, CancellationToken.None)).Order;
        }

        Assert.Contains(logs.Messages, message => message.Contains("predates response snapshots", StringComparison.Ordinal));
        var frozen = await ReadSnapshotAsync(database.ConnectionString);
        var savedResponse = frozen.Submissions.Single(row => row.Id == HistoricalSubmissionId).ResponseJson;
        Assert.Equal(JsonSerializer.Serialize(firstReplay, new JsonSerializerOptions(JsonSerializerDefaults.Web)), savedResponse);
        await ExecuteSqlAsync(database.ConnectionString,
            """UPDATE suppliers SET "Name" = 'Renamed historical supplier', "IsActive" = FALSE WHERE "Code" = 'NWIND' """);
        using (var scope = database.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PurchaseOrderService>();
            var replayed = Assert.IsType<CreateDraftResult.Created>(
                await service.CreateDraftAsync(request, HistoricalSubmissionId, CancellationToken.None)).Order;
            Assert.Equal(savedResponse, JsonSerializer.Serialize(replayed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        var afterReplay = await ReadSnapshotAsync(database.ConnectionString);
        Assert.Equal(frozen.Submissions, afterReplay.Submissions);
        Assert.Equal(original.OrdersJson, afterReplay.OrdersJson);
        Assert.Equal(original.LinesJson, afterReplay.LinesJson);
    }

    [Fact]
    public async Task Initializer_rolls_back_earlier_ddl_when_adding_snapshot_column_fails()
    {
        var interceptor = new FailInitializerAlterInterceptor();
        await using var database = await PostgresTestDatabase.CreateAsync(fixture, commandInterceptor: interceptor);
        await ExecuteSqlAsync(database.ConnectionString, LegacyDatabaseScript);
        await ExecuteSqlAsync(database.ConnectionString, "DROP TABLE purchase_order_submissions");

        using (var scope = database.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PurchasingDatabaseInitializer.InitializeAsync(db));
            Assert.Contains("Injected failure", exception.Message);
        }

        Assert.True(interceptor.SawSubmissionTableCreate);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT to_regclass('public.purchase_order_submissions') IS NULL,
                   to_regclass('public."IX_purchase_order_submissions_PurchaseOrderId"') IS NULL,
                   (SELECT COUNT(*) FROM purchase_orders),
                   (SELECT COUNT(*) FROM purchase_order_lines)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(41L, reader.GetInt64(2));
        Assert.Equal(42L, reader.GetInt64(3));
    }

    private static async Task ExecuteSqlAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<DatabaseSnapshot> ReadSnapshotAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM purchase_orders),
                (SELECT COUNT(*) FROM purchase_order_lines),
                (SELECT is_nullable = 'YES'
                 FROM information_schema.columns
                 WHERE table_schema = 'public'
                   AND table_name = 'purchase_order_submissions'
                   AND column_name = 'ResponseJson'),
                (SELECT jsonb_agg(to_jsonb(o) ORDER BY o."Id")::text FROM purchase_orders o),
                (SELECT jsonb_agg(to_jsonb(l) ORDER BY l."Id")::text FROM purchase_order_lines l)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var orderCount = reader.GetInt64(0);
        var lineCount = reader.GetInt64(1);
        var responseJsonNullable = reader.GetBoolean(2);
        var ordersJson = reader.GetString(3);
        var linesJson = reader.GetString(4);
        await reader.CloseAsync();

        var submissions = new List<SubmissionSnapshot>();
        await using (var ledger = new NpgsqlCommand("""
            SELECT "Id", "RequestPayload", "ResponseJson", "PurchaseOrderId"
            FROM purchase_order_submissions
            ORDER BY "Id"
            """, connection))
        await using (var ledgerReader = await ledger.ExecuteReaderAsync())
        {
            while (await ledgerReader.ReadAsync())
            {
                submissions.Add(new SubmissionSnapshot(
                    ledgerReader.GetGuid(0),
                    ledgerReader.GetString(1),
                    ledgerReader.IsDBNull(2) ? null : ledgerReader.GetString(2),
                    ledgerReader.GetGuid(3)));
            }
        }

        return new DatabaseSnapshot(orderCount, lineCount, responseJsonNullable, submissions, ordersJson, linesJson);
    }

    private const string LegacyDatabaseScript = """
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
            "Notes" character varying(2000),
            "CreatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_purchase_orders" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_purchase_orders_suppliers_SupplierCode"
                FOREIGN KEY ("SupplierCode") REFERENCES suppliers ("Code") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_purchase_orders_OrderNumber" ON purchase_orders ("OrderNumber");
        CREATE TABLE purchase_order_lines (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY,
            "PurchaseOrderId" uuid NOT NULL,
            "LineNumber" integer NOT NULL,
            "Sku" character varying(32) NOT NULL,
            "Description" character varying(200) NOT NULL,
            "Quantity" integer NOT NULL,
            "UnitPrice" numeric(18, 2) NOT NULL,
            CONSTRAINT "PK_purchase_order_lines" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_purchase_order_lines_purchase_orders_PurchaseOrderId"
                FOREIGN KEY ("PurchaseOrderId") REFERENCES purchase_orders ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX "IX_purchase_order_lines_PurchaseOrderId_LineNumber"
            ON purchase_order_lines ("PurchaseOrderId", "LineNumber");
        CREATE TABLE purchase_order_submissions (
            "Id" uuid NOT NULL,
            "RequestPayload" text NOT NULL,
            "PurchaseOrderId" uuid NOT NULL,
            "ResponseJson" text NOT NULL,
            CONSTRAINT pk_purchase_order_submissions PRIMARY KEY ("Id"),
            CONSTRAINT purchase_order_submissions_purchase_order_id_fkey
                FOREIGN KEY ("PurchaseOrderId") REFERENCES purchase_orders ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_purchase_order_submissions_PurchaseOrderId"
            ON purchase_order_submissions ("PurchaseOrderId");
        INSERT INTO suppliers ("Code", "Name", "Currency", "IsActive") VALUES
            ('ACME', 'Acme Industrial Supply', 'USD', TRUE),
            ('NWIND', 'Northwind Traders', 'USD', TRUE),
            ('CONTOSO', 'Contoso Components', 'EUR', TRUE),
            ('FABRIKAM', 'Fabrikam Fasteners', 'USD', FALSE);
        INSERT INTO purchase_orders ("Id", "OrderNumber", "SupplierCode", "Status", "Notes", "CreatedAt")
        SELECT ('10000000-0000-0000-0000-00000000000' || i)::uuid,
               'PO-LEGACY-' || lpad(i::text, 3, '0'),
               'NWIND',
               'Draft',
               CASE WHEN i = 1 THEN 'historic note' WHEN i = 2 THEN NULL ELSE 'legacy note ' || i END,
               '2024-01-02T03:04:05Z'::timestamptz + (i - 1) * interval '1 minute'
        FROM generate_series(1, 5) AS i;
        INSERT INTO purchase_orders ("Id", "OrderNumber", "SupplierCode", "Status", "Notes", "CreatedAt")
        SELECT md5('legacy-order-' || i)::uuid,
               'PO-LEGACY-' || lpad(i::text, 3, '0'),
               'NWIND',
               'Draft',
               'legacy note ' || i,
               '2024-01-02T03:04:05Z'::timestamptz + i * interval '1 minute'
        FROM generate_series(6, 41) AS i;
        INSERT INTO purchase_order_lines ("PurchaseOrderId", "LineNumber", "Sku", "Description", "Quantity", "UnitPrice")
        SELECT "Id",
               1,
               CASE WHEN "OrderNumber" = 'PO-LEGACY-001' THEN 'CHAI-24' ELSE 'LEGACY-' || "OrderNumber" END,
               CASE WHEN "OrderNumber" = 'PO-LEGACY-001' THEN 'Chai tea, case of 24' ELSE 'Legacy item' END,
               CASE WHEN "OrderNumber" = 'PO-LEGACY-001' THEN 4 ELSE 1 END,
               CASE WHEN "OrderNumber" = 'PO-LEGACY-001' THEN 18.00 ELSE 1.00 END
        FROM purchase_orders;
        INSERT INTO purchase_order_lines ("PurchaseOrderId", "LineNumber", "Sku", "Description", "Quantity", "UnitPrice")
        VALUES (
            '10000000-0000-0000-0000-000000000001', 2, 'MILK-2', 'Second legacy line', 2, 1.50
        );
        INSERT INTO purchase_order_submissions ("Id", "RequestPayload", "PurchaseOrderId", "ResponseJson")
        VALUES
            (
                '20000000-0000-0000-0000-000000000001',
                '{"supplierCode":"nwind","notes":" historic note ","lines":[{"sku":" chai-24 ","description":" Chai tea, case of 24 ","quantity":"4","unitPrice":"18.000"},{"sku":" milk-2 ","description":" Second legacy line ","quantity":"2","unitPrice":"1.500"}]}',
                '10000000-0000-0000-0000-000000000001',
                '{"id":"10000000-0000-0000-0000-000000000001","orderNumber":"PO-LEGACY-001","supplierCode":"NWIND","supplierName":"Northwind Traders","status":"Draft","notes":"historic note","total":75.00,"createdAt":"2024-01-02T03:04:05+00:00","lines":[{"lineNumber":1,"sku":"CHAI-24","description":"Chai tea, case of 24","quantity":4,"unitPrice":18.00,"lineTotal":72.00},{"lineNumber":2,"sku":"MILK-2","description":"Second legacy line","quantity":2,"unitPrice":1.50,"lineTotal":3.00}]}'
            ),
            (
                '20000000-0000-0000-0000-000000000002',
                '{"supplierCode":"NWIND","notes":"","lines":[{"sku":" legacy-po-legacy-002 ","description":" Legacy item ","quantity":1,"unitPrice":1.00}]}',
                '10000000-0000-0000-0000-000000000002',
                '{"id":"10000000-0000-0000-0000-000000000002","orderNumber":"PO-LEGACY-002","supplierCode":"NWIND","supplierName":"Northwind Traders","status":"Draft","notes":null,"total":1.00,"createdAt":"2024-01-02T03:05:05+00:00","lines":[{"lineNumber":1,"sku":"LEGACY-PO-LEGACY-002","description":"Legacy item","quantity":1,"unitPrice":1.00,"lineTotal":1.00}]}'
            ),
            (
                '20000000-0000-0000-0000-000000000003',
                '{"supplierCode":"NWIND","notes":"historic note 3","lines":[{"sku":"LEGACY-3","description":"Legacy item","quantity":1,"unitPrice":1.00}]}',
                '10000000-0000-0000-0000-000000000003',
                '{"id":"10000000-0000-0000-0000-000000000003","orderNumber":"PO-LEGACY-003","supplierCode":"NWIND","supplierName":"Northwind Traders","status":"Draft","notes":"legacy note 3","total":1.00,"createdAt":"2024-01-02T03:06:05+00:00","lines":[{"lineNumber":1,"sku":"LEGACY-PO-LEGACY-003","description":"Legacy item","quantity":1,"unitPrice":1.00,"lineTotal":1.00}]}'
            ),
            (
                '20000000-0000-0000-0000-000000000004',
                '{"supplierCode":"NWIND","notes":"historic note 4","lines":[{"sku":"LEGACY-4","description":"Legacy item","quantity":1,"unitPrice":1.00}]}',
                '10000000-0000-0000-0000-000000000004',
                '{"id":"10000000-0000-0000-0000-000000000004","orderNumber":"PO-LEGACY-004","supplierCode":"NWIND","supplierName":"Northwind Traders","status":"Draft","notes":"legacy note 4","total":1.00,"createdAt":"2024-01-02T03:07:05+00:00","lines":[{"lineNumber":1,"sku":"LEGACY-PO-LEGACY-004","description":"Legacy item","quantity":1,"unitPrice":1.00,"lineTotal":1.00}]}'
            ),
            (
                '20000000-0000-0000-0000-000000000005',
                '{"supplierCode":"NWIND","notes":"historic note 5","lines":[{"sku":"LEGACY-5","description":"Legacy item","quantity":1,"unitPrice":1.00}]}',
                '10000000-0000-0000-0000-000000000005',
                '{"id":"10000000-0000-0000-0000-000000000005","orderNumber":"PO-LEGACY-005","supplierCode":"NWIND","supplierName":"Northwind Traders","status":"Draft","notes":"legacy note 5","total":1.00,"createdAt":"2024-01-02T03:08:05+00:00","lines":[{"lineNumber":1,"sku":"LEGACY-PO-LEGACY-005","description":"Legacy item","quantity":1,"unitPrice":1.00,"lineTotal":1.00}]}'
            );
        """;

    private sealed record SubmissionSnapshot(Guid Id, string RequestPayload, string? ResponseJson, Guid PurchaseOrderId);
    private sealed record DatabaseSnapshot(long OrderCount, long LineCount, bool ResponseJsonNullable,
        IReadOnlyList<SubmissionSnapshot> Submissions, string OrdersJson, string LinesJson);
}
