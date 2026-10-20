using PurchaseOrders.Client;

namespace PurchaseOrders.WinForms;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        var options = PurchaseOrdersClientOptions.FromEnvironment();
        using var httpClient = new HttpClient { BaseAddress = options.BaseAddress };
        var apiClient = new PurchaseOrdersApiClient(httpClient, options);

        Application.Run(new PurchaseOrderForm(apiClient));
    }
}
