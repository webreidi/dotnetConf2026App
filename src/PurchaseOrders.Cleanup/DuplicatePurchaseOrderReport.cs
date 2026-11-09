using System.Text.Json;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.Cleanup;

public sealed record CleanupPair(Guid DuplicateId, Guid CanonicalId);

public sealed record CleanupLineSnapshot(
    int LineNumber,
    string Sku,
    string Description,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record CleanupOrderSnapshot(
    Guid Id,
    string OrderNumber,
    string SupplierCode,
    string SupplierName,
    string Status,
    string? Notes,
    decimal Total,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CleanupLineSnapshot> Lines);

public sealed record ParsedDuplicatePurchaseOrderReport(
    int ReportedRecordCount,
    IReadOnlyList<Guid> CanonicalIds,
    IReadOnlyList<CleanupPair> Pairs,
    IReadOnlyDictionary<Guid, CleanupOrderSnapshot> Orders)
{
    public int GroupCount => CanonicalIds.Count;
    public int ExpectedDeletionCount => Pairs.Count;
}

public static class DuplicatePurchaseOrderReportParser
{
    private const string Header = "# Duplicate purchase orders report";
    private const string MatchingRules =
        "Matches compare supplier code and name, status, notes, total, and every line item; PO ID, generated order number, and creation timestamp are excluded.";
    private const string CanonicalRule =
        "For review, the canonical record is the earliest-created order; ties are broken by ordinal UUID text. This choice is for review only, not proof of which record was original.";
    private const string Notice = "Saved draft purchase order for review only. This draft has not been sent to suppliers.";
    private const string NoDuplicates = "No duplicate purchase orders found.";

    public static ParsedDuplicatePurchaseOrderReport Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (markdown.Contains('\r') && markdown.Replace("\r\n", "", StringComparison.Ordinal).Contains('\r'))
        {
            throw Invalid("Bare carriage returns are not supported.");
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 9 || lines[0] != Header || lines[1] != "" ||
            lines[2] != MatchingRules || lines[3] != CanonicalRule ||
            lines[4] != "These saved drafts have not been sent to suppliers.")
        {
            throw Invalid("The Markdown header or duplicate identity rules do not match the supported report format.");
        }

        var fenceLine = 5;
        var hasNoDuplicatesNotice = lines[fenceLine] == NoDuplicates;
        if (hasNoDuplicatesNotice)
        {
            fenceLine++;
        }

        if (lines[fenceLine] != "" || lines[fenceLine + 1] != "```json")
        {
            throw Invalid("Expected exactly one JSON code fence after the report introduction.");
        }

        var closingFence = Array.IndexOf(lines, "```", fenceLine + 2);
        if (closingFence < 0 || closingFence != lines.Length - 2 || lines[^1] != "")
        {
            throw Invalid("The JSON code fence is missing, malformed, or followed by unsupported Markdown.");
        }

        var json = string.Join("\n", lines[(fenceLine + 2)..closingFence]);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            RejectDuplicateProperties(document.RootElement, "$");
            return ParseDocument(document.RootElement, hasNoDuplicatesNotice);
        }
        catch (JsonException ex)
        {
            throw Invalid($"The fenced JSON is malformed: {ex.Message}");
        }
    }

    private static ParsedDuplicatePurchaseOrderReport ParseDocument(JsonElement root, bool hasNoDuplicatesNotice)
    {
        RequireProperties(root, "$", "duplicateRecords", "duplicateGroups");
        var reportedRecordCount = ReadInt32(root.GetProperty("duplicateRecords"), "$.duplicateRecords");
        if (reportedRecordCount < 0)
        {
            throw Invalid("$.duplicateRecords cannot be negative.");
        }

        var groupsElement = root.GetProperty("duplicateGroups");
        if (groupsElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("$.duplicateGroups must be an array.");
        }

        var groups = new List<(Guid CanonicalId, List<CleanupOrderSnapshot> Orders, List<CleanupPair> Pairs)>();
        var allIds = new HashSet<Guid>();
        var totalReportedOrders = 0;
        foreach (var (groupElement, groupIndex) in groupsElement.EnumerateArray().Select((element, index) => (element, index)))
        {
            var groupPath = $"$.duplicateGroups[{groupIndex}]";
            RequireProperties(groupElement, groupPath, "canonicalId", "duplicatePairs", "orders");
            var canonicalId = ReadGuid(groupElement.GetProperty("canonicalId"), $"{groupPath}.canonicalId");

            var ordersElement = groupElement.GetProperty("orders");
            if (ordersElement.ValueKind != JsonValueKind.Array)
            {
                throw Invalid($"{groupPath}.orders must be an array.");
            }

            var orders = ordersElement.EnumerateArray()
                .Select((element, index) => ParseOrder(element, $"{groupPath}.orders[{index}]"))
                .ToList();
            if (orders.Count < 2)
            {
                throw Invalid($"{groupPath} must contain at least two orders.");
            }

            totalReportedOrders += orders.Count;
            foreach (var order in orders)
            {
                if (!allIds.Add(order.Id))
                {
                    throw Invalid($"Order ID {order.Id:D} is repeated or conflicts with a canonical ID.");
                }
            }

            if (orders[0].Id != canonicalId)
            {
                throw Invalid($"{groupPath}.canonicalId must identify the first, earliest-created order.");
            }

            for (var index = 1; index < orders.Count; index++)
            {
                if (CompareCanonicalOrder(orders[index - 1], orders[index]) > 0)
                {
                    throw Invalid($"{groupPath}.orders is not in the generator's canonical creation-time/UUID order.");
                }
                if (!SameDuplicateIdentity(orders[0], orders[index]))
                {
                    throw Invalid($"{groupPath} contains orders that do not match the reported duplicate identity rules.");
                }
            }

            var pairsElement = groupElement.GetProperty("duplicatePairs");
            if (pairsElement.ValueKind != JsonValueKind.Array)
            {
                throw Invalid($"{groupPath}.duplicatePairs must be an array.");
            }

            var pairs = pairsElement.EnumerateArray()
                .Select((element, index) => ParsePair(element, $"{groupPath}.duplicatePairs[{index}]", canonicalId))
                .ToList();
            if (pairs.Count != orders.Count - 1)
            {
                throw Invalid($"{groupPath}.duplicatePairs must identify every non-canonical order exactly once.");
            }

            for (var index = 0; index < pairs.Count; index++)
            {
                if (pairs[index].DuplicateId != orders[index + 1].Id ||
                    pairs[index].CanonicalId != canonicalId ||
                    pairs[index].DuplicateId == canonicalId)
                {
                    throw Invalid($"{groupPath}.duplicatePairs[{index}] conflicts with the canonical/order identifiers.");
                }
            }

            groups.Add((canonicalId, orders, pairs));
        }

        if (reportedRecordCount != totalReportedOrders)
        {
            throw Invalid("$.duplicateRecords must equal the number of reported group orders, including canonicals.");
        }
        if (hasNoDuplicatesNotice != (groups.Count == 0))
        {
            throw Invalid("The no-duplicates notice must appear if and only if the JSON contains no groups.");
        }

        for (var index = 1; index < groups.Count; index++)
        {
            var previous = groups[index - 1].Orders[0];
            var current = groups[index].Orders[0];
            if (CompareCanonicalOrder(previous, current) > 0)
            {
                throw Invalid("$.duplicateGroups is not in the generator's canonical creation-time/UUID order.");
            }
        }

        var snapshots = groups.SelectMany(group => group.Orders).ToDictionary(order => order.Id);
        return new ParsedDuplicatePurchaseOrderReport(
            reportedRecordCount,
            groups.Select(group => group.CanonicalId).ToArray(),
            groups.SelectMany(group => group.Pairs).ToArray(),
            snapshots);
    }

    private static CleanupOrderSnapshot ParseOrder(JsonElement wrapper, string path)
    {
        RequireProperties(wrapper, path, "notice", "order");
        if (ReadString(wrapper.GetProperty("notice"), $"{path}.notice") != Notice)
        {
            throw Invalid($"{path}.notice does not match the generator's fixed unsent-draft notice.");
        }

        var order = wrapper.GetProperty("order");
        var orderPath = $"{path}.order";
        RequireProperties(order, orderPath,
            "id", "orderNumber", "supplierCode", "supplierName", "status", "notes", "total", "createdAt", "lines");

        var linesElement = order.GetProperty("lines");
        if (linesElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{orderPath}.lines must be an array.");
        }

        var lines = linesElement.EnumerateArray()
            .Select((element, index) => ParseLine(element, $"{orderPath}.lines[{index}]"))
            .ToArray();
        if (lines.Length == 0)
        {
            throw Invalid($"{orderPath}.lines cannot be empty.");
        }
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].LineNumber != index + 1)
            {
                throw Invalid($"{orderPath}.lines must be ordered with contiguous line numbers starting at one.");
            }
        }

        var total = ReadDecimal(order.GetProperty("total"), $"{orderPath}.total");
        if (lines.Sum(line => line.LineTotal) != total)
        {
            throw Invalid($"{orderPath}.total does not equal the sum of its reported line totals.");
        }

        var createdAt = ReadDateTimeOffset(order.GetProperty("createdAt"), $"{orderPath}.createdAt");
        if (createdAt.Offset != TimeSpan.Zero)
        {
            throw Invalid($"{orderPath}.createdAt must be UTC, as emitted by the PostgreSQL-backed report generator.");
        }

        return new CleanupOrderSnapshot(
            ReadGuid(order.GetProperty("id"), $"{orderPath}.id"),
            ReadString(order.GetProperty("orderNumber"), $"{orderPath}.orderNumber"),
            ReadString(order.GetProperty("supplierCode"), $"{orderPath}.supplierCode"),
            ReadString(order.GetProperty("supplierName"), $"{orderPath}.supplierName"),
            ReadString(order.GetProperty("status"), $"{orderPath}.status"),
            ReadNullableString(order.GetProperty("notes"), $"{orderPath}.notes"),
            total,
            createdAt,
            lines);
    }

    private static CleanupLineSnapshot ParseLine(JsonElement line, string path)
    {
        RequireProperties(line, path, "lineNumber", "sku", "description", "quantity", "unitPrice", "lineTotal");
        var quantity = ReadInt32(line.GetProperty("quantity"), $"{path}.quantity");
        var unitPrice = ReadDecimal(line.GetProperty("unitPrice"), $"{path}.unitPrice");
        var lineTotal = ReadDecimal(line.GetProperty("lineTotal"), $"{path}.lineTotal");
        if (quantity <= 0 || unitPrice < 0 || lineTotal != decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero))
        {
            throw Invalid($"{path} has inconsistent quantity, unit price, or line total.");
        }

        return new CleanupLineSnapshot(
            ReadInt32(line.GetProperty("lineNumber"), $"{path}.lineNumber"),
            ReadString(line.GetProperty("sku"), $"{path}.sku"),
            ReadString(line.GetProperty("description"), $"{path}.description"),
            quantity,
            unitPrice,
            lineTotal);
    }

    private static CleanupPair ParsePair(JsonElement pair, string path, Guid groupCanonicalId)
    {
        RequireProperties(pair, path, "duplicateId", "canonicalId");
        var duplicateId = ReadGuid(pair.GetProperty("duplicateId"), $"{path}.duplicateId");
        var canonicalId = ReadGuid(pair.GetProperty("canonicalId"), $"{path}.canonicalId");
        if (canonicalId != groupCanonicalId)
        {
            throw Invalid($"{path}.canonicalId conflicts with its group's canonicalId.");
        }
        return new CleanupPair(duplicateId, canonicalId);
    }

    private static int CompareCanonicalOrder(CleanupOrderSnapshot left, CleanupOrderSnapshot right)
    {
        var createdComparison = left.CreatedAt.CompareTo(right.CreatedAt);
        return createdComparison != 0
            ? createdComparison
            : StringComparer.Ordinal.Compare(left.Id.ToString("D"), right.Id.ToString("D"));
    }

    private static bool SameDuplicateIdentity(CleanupOrderSnapshot left, CleanupOrderSnapshot right) =>
        left.SupplierCode == right.SupplierCode &&
        left.SupplierName == right.SupplierName &&
        left.Status == right.Status &&
        left.Notes == right.Notes &&
        left.Total == right.Total &&
        left.Lines.SequenceEqual(right.Lines);

    private static void RequireProperties(JsonElement element, string path, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{path} must be an object.");
        }

        var properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (properties.Length != expected.Length ||
            expected.Any(name => properties.Count(property => property == name) != 1) ||
            properties.Any(name => !expected.Contains(name, StringComparer.Ordinal)))
        {
            throw Invalid($"{path} has missing, unknown, or ambiguous properties.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw Invalid($"{path} contains duplicate JSON property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }

    private static string ReadString(JsonElement value, string path) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text
            : throw Invalid($"{path} must be a non-null string.");

    private static string? ReadNullableString(JsonElement value, string path) =>
        value.ValueKind == JsonValueKind.Null ? null : ReadString(value, path);

    private static Guid ReadGuid(JsonElement value, string path)
    {
        var text = ReadString(value, path);
        if (!Guid.TryParseExact(text, "D", out var id) ||
            id == Guid.Empty ||
            text != id.ToString("D"))
        {
            throw Invalid($"{path} must be a non-empty lowercase D-format UUID.");
        }
        return id;
    }

    private static int ReadInt32(JsonElement value, string path) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw Invalid($"{path} must be a 32-bit integer.");

    private static decimal ReadDecimal(JsonElement value, string path) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : throw Invalid($"{path} must be a decimal number.");

    private static DateTimeOffset ReadDateTimeOffset(JsonElement value, string path) =>
        value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var timestamp)
            ? timestamp
            : throw Invalid($"{path} must be an ISO-8601 timestamp.");

    private static FormatException Invalid(string message) => new(message);
}
