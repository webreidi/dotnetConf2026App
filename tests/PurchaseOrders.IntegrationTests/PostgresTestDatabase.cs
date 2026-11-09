using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PurchaseOrders.Api.Data;
using PurchaseOrders.Api.Services;

namespace PurchaseOrders.IntegrationTests;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly string _databaseName;

    private PostgresTestDatabase(IHost host, string connectionString, string databaseName)
    {
        _host = host;
        ConnectionString = connectionString;
        _databaseName = databaseName;
    }

    public string ConnectionString { get; }

    public IServiceScope CreateScope() => _host.Services.CreateScope();

    public static async Task<PostgresTestDatabase> CreateAsync(
        PurchaseOrdersAppFixture fixture,
        ISupplierOrderSimulator? simulator = null,
        DbCommandInterceptor? commandInterceptor = null,
        ILoggerProvider? loggerProvider = null,
        string databaseNamePrefix = "purchase_orders_test_")
    {
        var appConnectionString = await fixture.GetConnectionStringAsync();
        if (databaseNamePrefix.Length == 0 ||
            databaseNamePrefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("Database name prefix must contain only ASCII letters, digits, and underscores.", nameof(databaseNamePrefix));
        }
        var databaseName = $"{databaseNamePrefix}{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(appConnectionString)
        {
            Database = "postgres",
        };
        await using (var connection = new NpgsqlConnection(adminConnectionString.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(appConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        try
        {
            var builder = Host.CreateApplicationBuilder();
            if (loggerProvider is not null)
            {
                builder.Logging.AddProvider(loggerProvider);
            }
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:purchasingdb"] = connectionString,
            });
            builder.AddNpgsqlDbContext<PurchasingDbContext>(
                "purchasingdb",
                configureDbContextOptions: options =>
                {
                    if (commandInterceptor is not null)
                    {
                        options.AddInterceptors(commandInterceptor);
                    }

                });
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton<ISupplierOrderSimulator>(simulator ?? NoOpSupplierOrderSimulator.Instance);
            builder.Services.AddScoped<PurchaseOrderService>();

            return new PostgresTestDatabase(builder.Build(), connectionString, databaseName);
        }
        catch
        {
            await DropDatabaseAsync(adminConnectionString.ConnectionString, databaseName);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        using (var poolConnection = new NpgsqlConnection(ConnectionString))
        {
            NpgsqlConnection.ClearPool(poolConnection);
        }
        var adminConnectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = "postgres",
        };
        await DropDatabaseAsync(adminConnectionString.ConnectionString, _databaseName);
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\"", connection);
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed class NoOpSupplierOrderSimulator : ISupplierOrderSimulator
{
    public static NoOpSupplierOrderSimulator Instance { get; } = new();

    public Task PrepareDraftSubmissionAsync(
        PurchaseOrders.Api.Domain.PurchaseOrder order,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class ThrowOnceSupplierOrderSimulator(Func<Exception> exceptionFactory) : ISupplierOrderSimulator
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task PrepareDraftSubmissionAsync(
        PurchaseOrders.Api.Domain.PurchaseOrder order,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            throw exceptionFactory();
        }

        return Task.CompletedTask;
    }
}

internal sealed class FailInitializerAlterInterceptor : DbCommandInterceptor
{
    public bool SawSubmissionTableCreate { get; private set; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("CREATE TABLE IF NOT EXISTS purchase_order_submissions", StringComparison.OrdinalIgnoreCase))
        {
            SawSubmissionTableCreate = true;
        }

        if (command.CommandText.Contains("ALTER TABLE purchase_order_submissions", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Injected failure while adding the response snapshot column.");
        }

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}
