using System.Text.RegularExpressions;
using PurchaseOrders.Api.Domain;

namespace PurchaseOrders.UnitTests;

public class PurchaseOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 15, 30, 0, TimeSpan.Zero);
    private static readonly Supplier Acme = new() { Code = "ACME", Name = "Acme Industrial Supply", Currency = "USD" };

    [Fact]
    public void CreateDraft_sets_draft_status_and_numbers_lines()
    {
        var order = PurchaseOrder.CreateDraft(Acme, "PO-1", "  rush  ",
        [
            new("bolt-10", " Hex bolt ", 100, 0.35m),
            new("NUT-10", "Hex nut", 100, 0.12m),
        ], Now);

        Assert.Equal(PurchaseOrderStatus.Draft, order.Status);
        Assert.Equal("ACME", order.SupplierCode);
        Assert.Equal("rush", order.Notes);
        Assert.Equal(Now, order.CreatedAt);
        Assert.Equal([1, 2], order.Lines.Select(l => l.LineNumber));
        Assert.Equal("BOLT-10", order.Lines[0].Sku);
        Assert.Equal("Hex bolt", order.Lines[0].Description);
    }

    [Fact]
    public void Total_is_sum_of_rounded_line_totals()
    {
        var order = PurchaseOrder.CreateDraft(Acme, "PO-1", null,
        [
            new("A", "A", 3, 0.335m),
            new("B", "B", 2, 10.00m),
        ], Now);

        Assert.Equal(1.01m, order.Lines[0].LineTotal);
        Assert.Equal(21.01m, order.Total);
    }

    [Fact]
    public void Blank_notes_become_null()
    {
        var order = PurchaseOrder.CreateDraft(Acme, "PO-1", "   ", [new("A", "A", 1, 1m)], Now);

        Assert.Null(order.Notes);
    }

    [Fact]
    public void CreateDraft_rejects_inactive_supplier()
    {
        var inactive = new Supplier { Code = "OLD", Name = "Old Co", Currency = "USD", IsActive = false };

        Assert.Throws<InvalidOperationException>(() =>
            PurchaseOrder.CreateDraft(inactive, "PO-1", null, [new("A", "A", 1, 1m)], Now));
    }

    [Fact]
    public void CreateDraft_requires_lines()
    {
        Assert.Throws<InvalidOperationException>(() => PurchaseOrder.CreateDraft(Acme, "PO-1", null, [], Now));
    }

    [Fact]
    public void Order_number_contains_utc_date_and_random_suffix()
    {
        var first = OrderNumberGenerator.Create(Now);
        var second = OrderNumberGenerator.Create(Now);

        Assert.Matches(new Regex("^PO-20261110-[0-9A-F]{16}$"), first);
        Assert.True(first.Length <= 32);
        Assert.NotEqual(first, second);
    }
}
