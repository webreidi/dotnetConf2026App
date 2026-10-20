using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.Client;

public sealed class PurchaseOrdersApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly PurchaseOrdersClientOptions _options;

    public PurchaseOrdersApiClient(HttpClient http, PurchaseOrdersClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);

        _http = http;
        _options = options;
        _http.BaseAddress ??= options.BaseAddress;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>Raised before a request is re-sent after a timeout or transient failure.</summary>
    public event EventHandler<RetryEventArgs>? Retrying;

    public Task<IReadOnlyList<SupplierDto>> GetSuppliersAsync(CancellationToken cancellationToken = default) =>
        SendAsync(async ct =>
        {
            using var response = await _http.GetAsync("api/suppliers", ct);
            return await ReadAsync<IReadOnlyList<SupplierDto>>(response, ct);
        }, cancellationToken);

    public Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListPurchaseOrdersAsync(string? supplierCode = null, CancellationToken cancellationToken = default) =>
        SendAsync(async ct =>
        {
            var url = string.IsNullOrWhiteSpace(supplierCode)
                ? "api/purchase-orders"
                : $"api/purchase-orders?supplierCode={Uri.EscapeDataString(supplierCode)}";
            using var response = await _http.GetAsync(url, ct);
            return await ReadAsync<IReadOnlyList<PurchaseOrderSummaryDto>>(response, ct);
        }, cancellationToken);

    public Task<PurchaseOrderDto> CreateDraftAsync(CreatePurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(async ct =>
        {
            using var response = await _http.PostAsJsonAsync("api/purchase-orders", request, JsonOptions, ct);
            return await ReadAsync<PurchaseOrderDto>(response, ct);
        }, cancellationToken);
    }

    private async Task<T> SendAsync<T>(Func<CancellationToken, Task<T>> send, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_options.RequestTimeout);

            try
            {
                return await send(attemptCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= _options.MaxAttempts)
                {
                    throw new TimeoutException(
                        $"The purchase-order service did not respond within {_options.RequestTimeout.TotalSeconds:0.#}s after {attempt} attempts.");
                }

                await OnRetryingAsync(attempt, "timed out", cancellationToken);
            }
            catch (HttpRequestException ex) when (IsTransient(ex) && attempt < _options.MaxAttempts)
            {
                await OnRetryingAsync(attempt, ex.Message, cancellationToken);
            }
        }
    }

    private async Task OnRetryingAsync(int attempt, string reason, CancellationToken cancellationToken)
    {
        Retrying?.Invoke(this, new RetryEventArgs(attempt, _options.MaxAttempts, reason));
        await Task.Delay(_options.RetryDelay * attempt, cancellationToken);
    }

    private static bool IsTransient(HttpRequestException ex) =>
        ex.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw await PurchaseOrderValidationException.FromResponseAsync(response, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The purchase-order service returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The purchase-order service returned an empty response.");
    }
}

public sealed class RetryEventArgs(int attempt, int maxAttempts, string reason) : EventArgs
{
    public int Attempt { get; } = attempt;
    public int MaxAttempts { get; } = maxAttempts;
    public string Reason { get; } = reason;
}

public sealed class PurchaseOrderValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("The purchase order is not valid.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    internal static async Task<PurchaseOrderValidationException> FromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in errorsElement.EnumerateObject())
                {
                    errors[property.Name] = property.Value.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
                }
            }
        }
        catch (JsonException)
        {
        }

        if (errors.Count == 0)
        {
            errors["request"] = ["The request was rejected by the purchase-order service."];
        }

        return new PurchaseOrderValidationException(errors);
    }
}
