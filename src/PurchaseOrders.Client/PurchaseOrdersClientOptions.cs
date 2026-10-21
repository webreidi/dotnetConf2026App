namespace PurchaseOrders.Client;

public sealed class PurchaseOrdersClientOptions
{
    public Uri BaseAddress { get; set; } = new("https://localhost:7289");

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public int MaxAttempts { get; set; } = 3;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Resolves the API address from Aspire service discovery environment variables
    /// (injected by the AppHost via WithReference), falling back to PURCHASE_ORDERS_API_URL.
    /// </summary>
    public static PurchaseOrdersClientOptions FromEnvironment(Func<string, string?>? getVariable = null)
    {
        getVariable ??= Environment.GetEnvironmentVariable;
        var options = new PurchaseOrdersClientOptions();

        var address = getVariable("services__api__https__0")
            ?? getVariable("services__api__http__0")
            ?? getVariable("PURCHASE_ORDERS_API_URL");

        if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            options.BaseAddress = uri;
        }

        return options;
    }
}
