using Microsoft.Extensions.Options;
using PurchaseOrders.Api.Domain;

namespace PurchaseOrders.Api.Services;

public sealed class SupplierSimulationOptions
{
    public const string SectionName = "SupplierSimulation";

    public int BaseLatencyMs { get; set; } = 150;

    // Fraction (0..1) of supplier-order requests that are slow to prepare.
    public double SlowRequestRate { get; set; }

    public int SlowLatencyMs { get; set; } = 4_000;
}

public interface ISupplierOrderSimulator
{
    Task PrepareDraftSubmissionAsync(PurchaseOrder order, CancellationToken cancellationToken);
}

/// <summary>
/// Simulates the latency of preparing a supplier-order submission packet for a draft PO.
/// Nothing leaves the process: no supplier calls, payments, or emails are made.
/// </summary>
public sealed class SupplierOrderSimulator(
    IOptionsMonitor<SupplierSimulationOptions> options,
    ILogger<SupplierOrderSimulator> logger,
    TimeProvider timeProvider) : ISupplierOrderSimulator
{
    public async Task PrepareDraftSubmissionAsync(PurchaseOrder order, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var slow = settings.SlowRequestRate > 0 && Random.Shared.NextDouble() < settings.SlowRequestRate;
        var delay = TimeSpan.FromMilliseconds(slow ? settings.SlowLatencyMs : settings.BaseLatencyMs);

        logger.LogInformation(
            "Preparing simulated supplier submission for {OrderNumber} ({Supplier}); simulated latency {DelayMs} ms",
            order.OrderNumber, order.SupplierCode, delay.TotalMilliseconds);

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }
}
