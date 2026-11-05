using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PurchaseOrders.Api.Observability;

internal static class PurchaseOrderTelemetry
{
    private static readonly string InstrumentationName = typeof(PurchaseOrderTelemetry).Assembly.GetName().Name!;
    private static readonly Meter Meter = new(InstrumentationName);

    internal static readonly ActivitySource ActivitySource = new(InstrumentationName);

    private static readonly Counter<long> DraftCreations = Meter.CreateCounter<long>(
        "purchase_orders.draft_creation_attempts",
        description: "Number of purchase-order draft creation attempts by outcome.");

    private static readonly Histogram<double> SupplierPreparationDuration = Meter.CreateHistogram<double>(
        "purchase_orders.supplier_preparation.duration",
        unit: "s",
        description: "Duration of simulated supplier-order preparation.");

    internal static void RecordDraftCreation(string outcome)
    {
        var tags = new TagList { { "outcome", outcome } };
        DraftCreations.Add(1, in tags);
    }

    internal static void RecordSupplierPreparation(double durationSeconds, bool slow, string outcome)
    {
        var tags = new TagList
        {
            { "outcome", outcome },
            { "simulation.slow", slow },
        };
        SupplierPreparationDuration.Record(durationSeconds, in tags);
    }
}
