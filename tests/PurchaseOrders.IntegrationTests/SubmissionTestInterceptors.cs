using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PurchaseOrders.IntegrationTests;

internal sealed class ConcurrentSubmissionInsertInterceptor : DbCommandInterceptor
{
    private readonly TaskCompletionSource _bothInserting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _insertAttempts;

    public int InsertAttempts => Volatile.Read(ref _insertAttempts);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("INSERT INTO purchase_order_submissions", StringComparison.Ordinal))
        {
            if (Interlocked.Increment(ref _insertAttempts) == 2)
            {
                _bothInserting.TrySetResult();
            }

            await _bothInserting.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }

        return result;
    }

}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
    public void Dispose() { }

    private sealed class CapturingLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}
