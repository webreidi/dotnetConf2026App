using PurchaseOrders.Contracts;

namespace PurchaseOrders.Api.Domain;

public static class PurchaseOrderValidator
{
    public const int MaxLines = 50;
    public const int MaxQuantity = 10_000;
    public const decimal MaxUnitPrice = 1_000_000m;
    public const int MaxNotesLength = 500;

    public static Dictionary<string, string[]> Validate(CreatePurchaseOrderRequest? request)
    {
        var errors = new Dictionary<string, List<string>>();

        void Add(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                errors[key] = list = [];
            }

            list.Add(message);
        }

        if (request is null)
        {
            Add("request", "A request body is required.");
            return Flatten(errors);
        }

        if (string.IsNullOrWhiteSpace(request.SupplierCode))
        {
            Add(nameof(request.SupplierCode), "A supplier is required.");
        }

        if (request.Notes is { Length: > MaxNotesLength })
        {
            Add(nameof(request.Notes), $"Notes must be {MaxNotesLength} characters or fewer.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            Add(nameof(request.Lines), "At least one line is required.");
            return Flatten(errors);
        }

        if (request.Lines.Count > MaxLines)
        {
            Add(nameof(request.Lines), $"A purchase order may contain at most {MaxLines} lines.");
        }

        var seenSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var prefix = $"Lines[{i}]";

            if (line is null)
            {
                Add(prefix, "Line cannot be empty.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line.Sku))
            {
                Add($"{prefix}.Sku", "SKU is required.");
            }
            else if (line.Sku.Trim().Length > 32)
            {
                Add($"{prefix}.Sku", "SKU must be 32 characters or fewer.");
            }
            else if (!seenSkus.Add(line.Sku.Trim()))
            {
                Add($"{prefix}.Sku", $"SKU '{line.Sku.Trim()}' appears more than once.");
            }

            if (string.IsNullOrWhiteSpace(line.Description))
            {
                Add($"{prefix}.Description", "Description is required.");
            }
            else if (line.Description.Trim().Length > 200)
            {
                Add($"{prefix}.Description", "Description must be 200 characters or fewer.");
            }

            if (line.Quantity is < 1 or > MaxQuantity)
            {
                Add($"{prefix}.Quantity", $"Quantity must be between 1 and {MaxQuantity}.");
            }

            if (line.UnitPrice <= 0 || line.UnitPrice > MaxUnitPrice)
            {
                Add($"{prefix}.UnitPrice", $"Unit price must be greater than 0 and no more than {MaxUnitPrice:N0}.");
            }
            else if (decimal.Round(line.UnitPrice, 2) != line.UnitPrice)
            {
                Add($"{prefix}.UnitPrice", "Unit price may have at most two decimal places.");
            }
        }

        return Flatten(errors);
    }

    private static Dictionary<string, string[]> Flatten(Dictionary<string, List<string>> errors) =>
        errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
}
