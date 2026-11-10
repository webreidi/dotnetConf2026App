using PurchaseOrders.Api.Services;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.UnitTests;

public class PurchaseOrderDuplicateDetectorTests
{
    private static PurchaseOrderDto SampleOrder() => new(
        Guid.NewGuid(), "PO-1", "ACME", "Acme", "Draft", "Warehouse",
        12m, DateTimeOffset.UtcNow,
        [new(1, "BOLT", "Hex bolt", 2, 5m, 10m), new(2, "NUT", "Hex nut", 1, 2m, 2m)]);

    [Fact]
    public void Finds_every_member_of_every_duplicate_group_ignoring_identity_and_timestamp()
    {
        var first = SampleOrder();
        var second = first with { Id = Guid.NewGuid(), OrderNumber = "PO-2", CreatedAt = first.CreatedAt.AddDays(1), Lines = first.Lines.ToArray() };
        var third = first with { Id = Guid.NewGuid(), OrderNumber = "PO-3" };
        var other = first with { Id = Guid.NewGuid(), Notes = "Other warehouse" };
        var otherCopy = other with { Id = Guid.NewGuid(), OrderNumber = "PO-4" };
        var unique = first with { Id = Guid.NewGuid(), Notes = "Unique" };

        var duplicates = PurchaseOrderDuplicateDetector.FindDuplicates([first, second, unique, other, third, otherCopy]);

        Assert.Equal([first.Id, second.Id, third.Id, other.Id, otherCopy.Id], duplicates.Select(order => order.Id));
        var groups = PurchaseOrderDuplicateDetector.FindDuplicateGroups([first, second, unique, other, third, otherCopy, first]);
        Assert.Equal(2, groups.Count);
        Assert.Equal([first.Id, second.Id, third.Id], groups[0].Orders.Select(order => order.Id));
        Assert.Equal([other.Id, otherCopy.Id], groups[1].Orders.Select(order => order.Id));
    }

    [Theory]
    [InlineData("SupplierCode")]
    [InlineData("SupplierName")]
    [InlineData("Status")]
    [InlineData("Notes")]
    [InlineData("NullNotes")]
    [InlineData("Total")]
    [InlineData("LineCount")]
    [InlineData("LineNumber")]
    [InlineData("Sku")]
    [InlineData("Description")]
    [InlineData("Quantity")]
    [InlineData("UnitPrice")]
    [InlineData("LineTotal")]
    public void Does_not_match_when_any_business_field_differs(string field)
    {
        var order = SampleOrder();
        var line = order.Lines[1];
        var changed = field switch
        {
            "SupplierCode" => order with { SupplierCode = "OTHER" },
            "SupplierName" => order with { SupplierName = "Other" },
            "Status" => order with { Status = "Sent" },
            "Notes" => order with { Notes = "warehouse" },
            "NullNotes" => order with { Notes = null },
            "Total" => order with { Total = 13m },
            "LineCount" => order with { Lines = [order.Lines[0]] },
            "LineNumber" => order with { Lines = [order.Lines[0], line with { LineNumber = 3 }] },
            "Sku" => order with { Lines = [order.Lines[0], line with { Sku = "OTHER" }] },
            "Description" => order with { Lines = [order.Lines[0], line with { Description = "Other" }] },
            "Quantity" => order with { Lines = [order.Lines[0], line with { Quantity = 2 }] },
            "UnitPrice" => order with { Lines = [order.Lines[0], line with { UnitPrice = 3m }] },
            "LineTotal" => order with { Lines = [order.Lines[0], line with { LineTotal = 3m }] },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        changed = changed with { Id = Guid.NewGuid() };

        Assert.Empty(PurchaseOrderDuplicateDetector.FindDuplicates([order, changed]));
        Assert.Empty(PurchaseOrderDuplicateDetector.FindDuplicateGroups([order, changed]));
    }

    [Fact]
    public void Empty_unique_or_repeated_same_record_does_not_count_as_duplicates()
    {
        var order = SampleOrder();

        Assert.Empty(PurchaseOrderDuplicateDetector.FindDuplicates([]));
        Assert.Empty(PurchaseOrderDuplicateDetector.FindDuplicates([order]));
        Assert.Empty(PurchaseOrderDuplicateDetector.FindDuplicates([order, order]));
        Assert.Empty(PurchaseOrderDuplicateDetector.FindDuplicateGroups([]));
        Assert.Empty(PurchaseOrderDuplicateDetector.FindDuplicateGroups([order]));
        Assert.Empty(PurchaseOrderDuplicateDetector.FindDuplicateGroups([order, order]));
    }
}
