using Microsoft.EntityFrameworkCore;
using Npgsql;
using PurchaseOrders.Api.Data;
using PurchaseOrders.Cleanup;
using Xunit.Abstractions;

namespace PurchaseOrders.IntegrationTests;

[Collection(PurchaseOrdersAppCollection.Name)]
public sealed class PurchaseOrderCleanupIntegrationTests(
    PurchaseOrdersAppFixture fixture,
    ITestOutputHelper output)
{
    private const string DisposableDatabasePrefix = "purchase_orders_disposable_";

    [Fact]
    public async Task Valid_report_previews_then_deletes_only_reported_duplicates_and_preserves_canonicals()
    {
        var reportPath = FindReportPath();
        var report = DuplicatePurchaseOrderReportParser.Parse(await File.ReadAllTextAsync(reportPath));
        await using var database = await PostgresTestDatabase.CreateAsync(
            fixture, databaseNamePrefix: DisposableDatabasePrefix);
        await InitializeAndSeedAsync(database, report);

        using var previewOutput = new StringWriter();
        using var previewError = new StringWriter();
        var previewExitCode = await PurchaseOrderCleanupCommand.RunAsync(
            CreateArgs(reportPath, database.ConnectionString),
            previewOutput,
            previewError);
        output.WriteLine("PREVIEW:");
        output.WriteLine(previewOutput.ToString());
        Assert.Equal(0, previewExitCode);
        Assert.Empty(previewError.ToString());
        Assert.Contains($"Expected purchase-order deletions: {report.ExpectedDeletionCount}", previewOutput.ToString());
        foreach (var pair in report.Pairs)
        {
            Assert.Contains($"delete {pair.DuplicateId:D} -> retain {pair.CanonicalId:D}", previewOutput.ToString());
        }

        using var executeOutput = new StringWriter();
        using var executeError = new StringWriter();
        var executeExitCode = await PurchaseOrderCleanupCommand.RunAsync(
            CreateArgs(reportPath, database.ConnectionString, execute: true),
            executeOutput,
            executeError);
        output.WriteLine("EXECUTION:");
        output.WriteLine(executeOutput.ToString());
        Assert.Equal(0, executeExitCode);
        Assert.Empty(executeError.ToString());
        Assert.Contains($"Result: SUCCESS (transaction committed)", executeOutput.ToString());
        Assert.Contains($"Deleted purchase orders: {report.ExpectedDeletionCount}", executeOutput.ToString());

        using var scope = database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
        Assert.Equal(report.Orders.Count - report.ExpectedDeletionCount, await db.PurchaseOrders.CountAsync());
        Assert.Equal(
            report.Orders.Values.Sum(order => order.Lines.Count) -
                report.Pairs.Sum(pair => report.Orders[pair.DuplicateId].Lines.Count),
            await db.Set<PurchaseOrders.Api.Domain.PurchaseOrderLine>().CountAsync());
        foreach (var pair in report.Pairs)
        {
            Assert.False(await db.PurchaseOrders.AnyAsync(order => order.Id == pair.DuplicateId));
            Assert.False(await db.Set<PurchaseOrders.Api.Domain.PurchaseOrderLine>()
                .AnyAsync(line => line.PurchaseOrderId == pair.DuplicateId));
        }
        foreach (var canonicalId in report.CanonicalIds)
        {
            var actual = await db.PurchaseOrders
                .Include(order => order.Lines)
                .SingleAsync(order => order.Id == canonicalId);
            var expected = report.Orders[canonicalId];
            Assert.Equal(expected.OrderNumber, actual.OrderNumber);
            Assert.Equal(expected.SupplierCode, actual.SupplierCode);
            Assert.Equal(expected.Notes, actual.Notes);
            Assert.Equal(expected.CreatedAt, actual.CreatedAt);
            Assert.Equal(expected.Total, actual.Total);
            Assert.Equal(expected.Lines.Count, actual.Lines.Count);
        }
    }

    [Fact]
    public async Task Stale_report_is_rejected_without_deleting_any_rows()
    {
        var reportPath = FindReportPath();
        var report = DuplicatePurchaseOrderReportParser.Parse(await File.ReadAllTextAsync(reportPath));
        await using var database = await PostgresTestDatabase.CreateAsync(
            fixture, databaseNamePrefix: DisposableDatabasePrefix);
        await InitializeAndSeedAsync(database, report);

        var changedId = report.Pairs[0].DuplicateId;
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """UPDATE purchase_orders SET "Notes" = 'changed after report' WHERE "Id" = @id""",
                connection);
            command.Parameters.AddWithValue("id", changedId);
            await command.ExecuteNonQueryAsync();
        }

        using var previewOutput = new StringWriter();
        using var previewError = new StringWriter();
        var exitCode = await PurchaseOrderCleanupCommand.RunAsync(
            CreateArgs(reportPath, database.ConnectionString),
            previewOutput,
            previewError);

        Assert.Equal(3, exitCode);
        Assert.Contains("changed after the report was generated", previewError.ToString());
        Assert.Contains("Failed items:", previewError.ToString());
        using var scope = database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
        Assert.Equal(report.Orders.Count, await db.PurchaseOrders.CountAsync());
        Assert.Equal(report.Orders.Values.Sum(order => order.Lines.Count),
            await db.Set<PurchaseOrders.Api.Domain.PurchaseOrderLine>().CountAsync());
    }

    [Fact]
    public async Task Duplicate_with_submission_identity_is_not_deleted()
    {
        var reportPath = FindReportPath();
        var report = DuplicatePurchaseOrderReportParser.Parse(await File.ReadAllTextAsync(reportPath));
        await using var database = await PostgresTestDatabase.CreateAsync(
            fixture, databaseNamePrefix: DisposableDatabasePrefix);
        await InitializeAndSeedAsync(database, report);

        var duplicateId = report.Pairs[0].DuplicateId;
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO purchase_order_submissions ("Id", "RequestPayload", "PurchaseOrderId", "ResponseJson")
                VALUES (@id, '{}', @purchaseOrderId, NULL)
                """, connection);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("purchaseOrderId", duplicateId);
            await command.ExecuteNonQueryAsync();
        }

        using var outputWriter = new StringWriter();
        using var errorWriter = new StringWriter();
        var exitCode = await PurchaseOrderCleanupCommand.RunAsync(
            CreateArgs(reportPath, database.ConnectionString),
            outputWriter,
            errorWriter);

        Assert.Equal(3, exitCode);
        Assert.Contains("durable submission identity", errorWriter.ToString());
        using var scope = database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
        Assert.Equal(report.Orders.Count, await db.PurchaseOrders.CountAsync());
        Assert.Equal(1, await db.PurchaseOrderSubmissions.CountAsync());
        Assert.True(await db.PurchaseOrders.AnyAsync(order => order.Id == duplicateId));
    }

    private static async Task InitializeAndSeedAsync(
        PostgresTestDatabase database,
        ParsedDuplicatePurchaseOrderReport report)
    {
        using (var scope = database.CreateScope())
        {
            await PurchasingDatabaseInitializer.InitializeAsync(
                scope.ServiceProvider.GetRequiredService<PurchasingDbContext>());
        }

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var order in report.Orders.Values)
        {
            await using (var command = new NpgsqlCommand("""
                INSERT INTO purchase_orders ("Id", "OrderNumber", "SupplierCode", "Status", "Notes", "CreatedAt")
                VALUES (@id, @orderNumber, @supplierCode, @status, @notes, @createdAt)
                """, connection))
            {
                command.Parameters.AddWithValue("id", order.Id);
                command.Parameters.AddWithValue("orderNumber", order.OrderNumber);
                command.Parameters.AddWithValue("supplierCode", order.SupplierCode);
                command.Parameters.AddWithValue("status", order.Status);
                command.Parameters.AddWithValue("notes", (object?)order.Notes ?? DBNull.Value);
                command.Parameters.AddWithValue("createdAt", order.CreatedAt);
                await command.ExecuteNonQueryAsync();
            }

            foreach (var line in order.Lines)
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO purchase_order_lines
                        ("PurchaseOrderId", "LineNumber", "Sku", "Description", "Quantity", "UnitPrice")
                    VALUES (@purchaseOrderId, @lineNumber, @sku, @description, @quantity, @unitPrice)
                    """, connection);
                command.Parameters.AddWithValue("purchaseOrderId", order.Id);
                command.Parameters.AddWithValue("lineNumber", line.LineNumber);
                command.Parameters.AddWithValue("sku", line.Sku);
                command.Parameters.AddWithValue("description", line.Description);
                command.Parameters.AddWithValue("quantity", line.Quantity);
                command.Parameters.AddWithValue("unitPrice", line.UnitPrice);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private static string[] CreateArgs(string reportPath, string connectionString, bool execute = false)
    {
        var args = new List<string>
        {
            "--report", reportPath,
            "--database", connectionString,
            "--environment", "disposable",
        };
        if (execute)
        {
            args.Add("--execute");
            args.Add("--confirm");
            args.Add(PurchaseOrderCleanupCommand.ConfirmationPhrase);
        }
        return args.ToArray();
    }

    private static string FindReportPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Reports", "duplicate-purchase-orders.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("Could not locate Reports/duplicate-purchase-orders.md from the test output directory.");
    }
}
