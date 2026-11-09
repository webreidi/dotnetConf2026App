using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PurchaseOrders.Api.Data;

namespace PurchaseOrders.Cleanup;

public static class PurchaseOrderCleanupCommand
{
    public const string ConfirmationPhrase = "DELETE-REPORTED-DUPLICATES";
    private const string DisposableDatabasePrefix = "purchase_orders_disposable_";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = ParseArguments(args);
            var target = ValidateTarget(options.Environment, options.ConnectionString);
            var markdown = await File.ReadAllTextAsync(options.ReportPath, cancellationToken);
            var report = DuplicatePurchaseOrderReportParser.Parse(markdown);
            var dbOptions = new DbContextOptionsBuilder<PurchasingDbContext>()
                .UseNpgsql(target.ConnectionString)
                .Options;

            await using var db = new PurchasingDbContext(dbOptions);
            await db.Database.OpenConnectionAsync(cancellationToken);
            await VerifyConnectedDatabaseAsync(db, target.DatabaseName, cancellationToken);
            var cleanup = new PurchaseOrderCleanupService(db);

            if (!options.Execute)
            {
                var plan = await cleanup.PreviewAsync(report, cancellationToken);
                WritePlan(output, target, plan, "PREVIEW (read-only; no changes made)");
                await output.WriteLineAsync("Skipped items: 0");
                await output.WriteLineAsync("Failed items: 0");
                return 0;
            }

            try
            {
                var result = await cleanup.ExecuteAsync(report, cancellationToken);
                WritePlan(output, target, new CleanupPlan(
                    report.GroupCount,
                    report.ReportedRecordCount,
                    report.Pairs,
                    result.DeletedLines), "EXECUTION");
                await output.WriteLineAsync("Result: SUCCESS (transaction committed)");
                await output.WriteLineAsync($"Deleted purchase orders: {result.DeletedPurchaseOrders}");
                await output.WriteLineAsync($"Deleted related lines (cascade): {result.DeletedLines}");
                await output.WriteLineAsync($"Canonical purchase orders preserved: {result.PreservedCanonicalOrders}");
                await output.WriteLineAsync("Skipped items: 0");
                await output.WriteLineAsync("Failed items: 0");
                return 0;
            }
            catch (CleanupValidationException ex)
            {
                await WriteFailureAsync(error, "FAILED: report/database validation rejected the cleanup; no success is reported.", ex.Issues);
                return 3;
            }
            catch (CleanupExecutionException ex)
            {
                await error.WriteLineAsync($"FAILED: {ex.Message}");
                await error.WriteLineAsync("No success is reported. Verify the disposable target before retrying.");
                await error.WriteLineAsync("Skipped items: 0");
                await error.WriteLineAsync("Failed items: transaction");
                return 4;
            }
        }
        catch (CleanupValidationException ex)
        {
            await WriteFailureAsync(error, "FAILED: report/database validation rejected the cleanup; no success is reported.", ex.Issues);
            return 3;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Cancelled; no success is reported.");
            return 130;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or
                                   UnauthorizedAccessException or NpgsqlException or DbUpdateException or
                                   InvalidOperationException)
        {
            await error.WriteLineAsync($"FAILED: {ex.Message}");
            await error.WriteLineAsync("No success is reported.");
            return 2;
        }
    }

    private static Options ParseArguments(IReadOnlyList<string> args)
    {
        string? reportPath = null;
        string? connectionString = null;
        string? environment = null;
        string? confirmation = null;
        var execute = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument == "--execute")
            {
                if (!seen.Add(argument))
                {
                    throw new ArgumentException("--execute may only be specified once.");
                }
                execute = true;
                continue;
            }

            if (argument is not ("--report" or "--database" or "--environment" or "--confirm"))
            {
                throw new ArgumentException($"Unknown argument '{argument}'.");
            }
            if (!seen.Add(argument) || index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"Argument '{argument}' must appear once with a non-empty value.");
            }

            var value = args[++index];
            switch (argument)
            {
                case "--report":
                    reportPath = value;
                    break;
                case "--database":
                    connectionString = value;
                    break;
                case "--environment":
                    environment = value;
                    break;
                case "--confirm":
                    confirmation = value;
                    break;
            }
        }

        if (reportPath is null || connectionString is null || environment is null)
        {
            throw new ArgumentException(
                "Required arguments: --report <path> --database <PostgreSQL connection string> --environment disposable.");
        }
        if (execute && confirmation != ConfirmationPhrase)
        {
            throw new ArgumentException($"Execution requires --confirm {ConfirmationPhrase}.");
        }
        if (!execute && confirmation is not null)
        {
            throw new ArgumentException("--confirm is only valid with --execute.");
        }
        if (string.IsNullOrWhiteSpace(Path.GetFullPath(reportPath)))
        {
            throw new ArgumentException("--report must be an explicit file path.");
        }

        return new Options(reportPath, connectionString, environment, execute);
    }

    private static Target ValidateTarget(string environment, string connectionString)
    {
        if (environment != "disposable")
        {
            throw new ArgumentException(
                $"Environment '{environment}' is not recognized. Only --environment disposable is permitted.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = builder.Database ?? "";
        if (!databaseName.StartsWith(DisposableDatabasePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(databaseName[DisposableDatabasePrefix.Length..], "N", out var databaseId) ||
            databaseId == Guid.Empty ||
            databaseName[DisposableDatabasePrefix.Length..] != databaseId.ToString("N"))
        {
            throw new ArgumentException(
                $"Database name must be {DisposableDatabasePrefix}<32-hex-guid>; production and unrecognized targets are refused.");
        }

        var host = (builder.Host ?? "").Trim('[', ']');
        var isLoopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        if (!isLoopback)
        {
            throw new ArgumentException("Disposable cleanup is restricted to a loopback PostgreSQL host.");
        }

        return new Target(builder.ConnectionString, databaseName);
    }

    private static async Task VerifyConnectedDatabaseAsync(
        PurchasingDbContext db,
        string expectedDatabase,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT current_database()",
            (NpgsqlConnection)db.Database.GetDbConnection());
        var actualDatabase = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(actualDatabase, expectedDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Connected database '{actualDatabase}' does not match the explicitly requested disposable database.");
        }
    }

    private static async Task WriteFailureAsync(
        TextWriter error,
        string title,
        IReadOnlyList<CleanupIssue> issues)
    {
        await error.WriteLineAsync(title);
        foreach (var issue in issues)
        {
            var pair = issue.DuplicateId is { } duplicateId && issue.CanonicalId is { } canonicalId
                ? $"{duplicateId:D} -> retain {canonicalId:D}: "
                : "";
            await error.WriteLineAsync($"- {pair}{issue.Message}");
        }
        await error.WriteLineAsync("Skipped items: 0");
        await error.WriteLineAsync($"Failed items: {issues.Count}");
    }

    private static void WritePlan(TextWriter output, Target target, CleanupPlan plan, string mode)
    {
        output.WriteLine($"Environment: disposable");
        output.WriteLine($"Database: {target.DatabaseName}");
        output.WriteLine($"Mode: {mode}");
        output.WriteLine($"Reported duplicate groups: {plan.GroupCount}");
        output.WriteLine($"Reported group records (including canonicals): {plan.ReportedRecordCount}");
        output.WriteLine("Proposed duplicate removals:");
        foreach (var pair in plan.Pairs)
        {
            output.WriteLine($"  delete {pair.DuplicateId:D} -> retain {pair.CanonicalId:D}");
        }
        output.WriteLine($"Expected purchase-order deletions: {plan.Pairs.Count}");
        output.WriteLine($"Expected related-line deletions (cascade): {plan.ExpectedLineDeletionCount}");
    }

    private sealed record Options(string ReportPath, string ConnectionString, string Environment, bool Execute);
    private sealed record Target(string ConnectionString, string DatabaseName);
}
