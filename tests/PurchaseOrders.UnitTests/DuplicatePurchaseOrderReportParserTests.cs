using PurchaseOrders.Cleanup;

namespace PurchaseOrders.UnitTests;

public sealed class DuplicatePurchaseOrderReportParserTests
{
    private const string ValidReport = """
        # Duplicate purchase orders report

        Matches compare supplier code and name, status, notes, total, and every line item; PO ID, generated order number, and creation timestamp are excluded.
        For review, the canonical record is the earliest-created order; ties are broken by ordinal UUID text. This choice is for review only, not proof of which record was original.
        These saved drafts have not been sent to suppliers.

        ```json
        {
          "duplicateRecords": 2,
          "duplicateGroups": [
            {
              "canonicalId": "00000000-0000-0000-0000-000000000001",
              "duplicatePairs": [
                {
                  "duplicateId": "00000000-0000-0000-0000-000000000002",
                  "canonicalId": "00000000-0000-0000-0000-000000000001"
                }
              ],
              "orders": [
                {
                  "notice": "Saved draft purchase order for review only. This draft has not been sent to suppliers.",
                  "order": {
                    "id": "00000000-0000-0000-0000-000000000001",
                    "orderNumber": "PO-ONE",
                    "supplierCode": "ACME",
                    "supplierName": "Acme Industrial Supply",
                    "status": "Draft",
                    "notes": null,
                    "total": 10.00,
                    "createdAt": "2026-11-01T10:00:00+00:00",
                    "lines": [
                      {
                        "lineNumber": 1,
                        "sku": "BOLT",
                        "description": "Hex bolt",
                        "quantity": 1,
                        "unitPrice": 10.00,
                        "lineTotal": 10.00
                      }
                    ]
                  }
                },
                {
                  "notice": "Saved draft purchase order for review only. This draft has not been sent to suppliers.",
                  "order": {
                    "id": "00000000-0000-0000-0000-000000000002",
                    "orderNumber": "PO-TWO",
                    "supplierCode": "ACME",
                    "supplierName": "Acme Industrial Supply",
                    "status": "Draft",
                    "notes": null,
                    "total": 10.00,
                    "createdAt": "2026-11-02T10:00:00+00:00",
                    "lines": [
                      {
                        "lineNumber": 1,
                        "sku": "BOLT",
                        "description": "Hex bolt",
                        "quantity": 1,
                        "unitPrice": 10.00,
                        "lineTotal": 10.00
                      }
                    ]
                  }
                }
              ]
            }
          ]
        }
        ```
        """ + "\n";

    [Fact]
    public void Parse_accepts_generator_format_and_explicit_duplicate_pair()
    {
        var report = DuplicatePurchaseOrderReportParser.Parse(ValidReport);

        Assert.Equal(2, report.ReportedRecordCount);
        Assert.Equal(1, report.GroupCount);
        var pair = Assert.Single(report.Pairs);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000002"), pair.DuplicateId);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), pair.CanonicalId);
        Assert.Equal(1, report.ExpectedDeletionCount);
    }

    [Fact]
    public void Parse_rejects_malformed_markdown_structure()
    {
        var malformed = ValidReport.Replace("```json", "```yaml", StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => DuplicatePurchaseOrderReportParser.Parse(malformed));
    }

    [Fact]
    public void Parse_rejects_conflicting_identifiers_and_duplicate_json_properties()
    {
        var conflicting = ValidReport.Replace(
            "\"duplicateId\": \"00000000-0000-0000-0000-000000000002\"",
            "\"duplicateId\": \"00000000-0000-0000-0000-000000000001\"",
            StringComparison.Ordinal);
        var ambiguous = ValidReport.Replace(
            "\"duplicateId\": \"00000000-0000-0000-0000-000000000002\",",
            "\"duplicateId\": \"00000000-0000-0000-0000-000000000002\",\n                  \"duplicateId\": \"00000000-0000-0000-0000-000000000002\",",
            StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => DuplicatePurchaseOrderReportParser.Parse(conflicting));
        Assert.Throws<FormatException>(() => DuplicatePurchaseOrderReportParser.Parse(ambiguous));
    }
}
