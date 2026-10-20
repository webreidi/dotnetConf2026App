using PurchaseOrders.Api.Domain;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.UnitTests;

public class PurchaseOrderValidatorTests
{
    private static CreatePurchaseOrderRequest ValidRequest(params PurchaseOrderLineRequest[] lines) =>
        new("ACME", "Deliver to dock 4", lines.Length > 0 ? lines : [new("BOLT-10", "Hex bolt M10", 100, 0.35m)]);

    [Fact]
    public void Valid_request_has_no_errors()
    {
        Assert.Empty(PurchaseOrderValidator.Validate(ValidRequest()));
    }

    [Fact]
    public void Null_request_is_rejected()
    {
        Assert.Contains("request", PurchaseOrderValidator.Validate(null).Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Supplier_is_required(string supplierCode)
    {
        var errors = PurchaseOrderValidator.Validate(ValidRequest() with { SupplierCode = supplierCode });

        Assert.Contains(nameof(CreatePurchaseOrderRequest.SupplierCode), errors.Keys);
    }

    [Fact]
    public void At_least_one_line_is_required()
    {
        var errors = PurchaseOrderValidator.Validate(new CreatePurchaseOrderRequest("ACME", null, []));

        Assert.Contains(nameof(CreatePurchaseOrderRequest.Lines), errors.Keys);
    }

    [Fact]
    public void Too_many_lines_are_rejected()
    {
        var lines = Enumerable.Range(1, PurchaseOrderValidator.MaxLines + 1)
            .Select(i => new PurchaseOrderLineRequest($"SKU-{i}", "Item", 1, 1m))
            .ToArray();

        var errors = PurchaseOrderValidator.Validate(ValidRequest(lines));

        Assert.Contains(nameof(CreatePurchaseOrderRequest.Lines), errors.Keys);
    }

    [Fact]
    public void Duplicate_skus_are_rejected_case_insensitively()
    {
        var errors = PurchaseOrderValidator.Validate(ValidRequest(
            new("bolt-10", "Hex bolt", 1, 1m),
            new("BOLT-10", "Hex bolt again", 2, 1m)));

        Assert.Contains("Lines[1].Sku", errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(PurchaseOrderValidator.MaxQuantity + 1)]
    public void Quantity_must_be_in_range(int quantity)
    {
        var errors = PurchaseOrderValidator.Validate(ValidRequest(new PurchaseOrderLineRequest("NUT-10", "Nut", quantity, 1m)));

        Assert.Contains("Lines[0].Quantity", errors.Keys);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000000.01")]
    [InlineData("1.005")]
    public void Unit_price_must_be_positive_bounded_and_two_decimals(string price)
    {
        var errors = PurchaseOrderValidator.Validate(ValidRequest(new PurchaseOrderLineRequest("NUT-10", "Nut", 1, decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture))));

        Assert.Contains("Lines[0].UnitPrice", errors.Keys);
    }

    [Fact]
    public void Missing_sku_and_description_are_reported_per_line()
    {
        var errors = PurchaseOrderValidator.Validate(ValidRequest(new PurchaseOrderLineRequest(" ", "", 1, 1m)));

        Assert.Contains("Lines[0].Sku", errors.Keys);
        Assert.Contains("Lines[0].Description", errors.Keys);
    }

    [Fact]
    public void Notes_longer_than_limit_are_rejected()
    {
        var errors = PurchaseOrderValidator.Validate(ValidRequest() with { Notes = new string('x', PurchaseOrderValidator.MaxNotesLength + 1) });

        Assert.Contains(nameof(CreatePurchaseOrderRequest.Notes), errors.Keys);
    }
}

