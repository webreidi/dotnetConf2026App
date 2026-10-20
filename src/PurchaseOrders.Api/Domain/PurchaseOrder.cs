namespace PurchaseOrders.Api.Domain;

public sealed class Supplier
{
    public required string Code { get; init; }
    public required string Name { get; set; }
    public required string Currency { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum PurchaseOrderStatus
{
    Draft = 0,
}

public sealed class PurchaseOrder
{
    private readonly List<PurchaseOrderLine> _lines = [];

    private PurchaseOrder()
    {
    }

    public Guid Id { get; private set; }
    public string OrderNumber { get; private set; } = string.Empty;
    public string SupplierCode { get; private set; } = string.Empty;
    public Supplier? Supplier { get; private set; }
    public PurchaseOrderStatus Status { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<PurchaseOrderLine> Lines => _lines;

    public decimal Total => _lines.Sum(l => l.LineTotal);

    public static PurchaseOrder CreateDraft(
        Supplier supplier,
        string orderNumber,
        string? notes,
        IEnumerable<NewPurchaseOrderLine> lines,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNumber);
        ArgumentNullException.ThrowIfNull(lines);

        if (!supplier.IsActive)
        {
            throw new InvalidOperationException($"Supplier '{supplier.Code}' is not active.");
        }

        var order = new PurchaseOrder
        {
            Id = Guid.CreateVersion7(createdAt),
            OrderNumber = orderNumber,
            SupplierCode = supplier.Code,
            Supplier = supplier,
            Status = PurchaseOrderStatus.Draft,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedAt = createdAt,
        };

        var lineNumber = 1;
        foreach (var line in lines)
        {
            order._lines.Add(new PurchaseOrderLine(
                lineNumber++,
                line.Sku.Trim().ToUpperInvariant(),
                line.Description.Trim(),
                line.Quantity,
                line.UnitPrice));
        }

        if (order._lines.Count == 0)
        {
            throw new InvalidOperationException("A purchase order requires at least one line.");
        }

        return order;
    }
}

public sealed class PurchaseOrderLine
{
    private PurchaseOrderLine()
    {
    }

    internal PurchaseOrderLine(int lineNumber, string sku, string description, int quantity, decimal unitPrice)
    {
        LineNumber = lineNumber;
        Sku = sku;
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public int Id { get; private set; }
    public Guid PurchaseOrderId { get; private set; }
    public int LineNumber { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }

    public decimal LineTotal => decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
}

public sealed record NewPurchaseOrderLine(string Sku, string Description, int Quantity, decimal UnitPrice);
