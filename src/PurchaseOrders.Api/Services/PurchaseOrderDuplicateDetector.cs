using PurchaseOrders.Contracts;

namespace PurchaseOrders.Api.Services;

public static class PurchaseOrderDuplicateDetector
{
    public static IReadOnlyList<PurchaseOrderDto> FindDuplicates(IEnumerable<PurchaseOrderDto> orders) =>
        FindDuplicateGroups(orders).SelectMany(group => group).ToList();

    public static IReadOnlyList<IReadOnlyList<PurchaseOrderDto>> FindDuplicateGroups(IEnumerable<PurchaseOrderDto> orders) =>
        orders
            .DistinctBy(order => order.Id)
            .GroupBy(order => order, new ContentComparer())
            .Where(group => group.Count() > 1)
            .Select(group => (IReadOnlyList<PurchaseOrderDto>)group.ToList())
            .ToList();

    private sealed class ContentComparer : IEqualityComparer<PurchaseOrderDto>
    {
        public bool Equals(PurchaseOrderDto? x, PurchaseOrderDto? y) =>
            ReferenceEquals(x, y) ||
            x is not null && y is not null &&
            x.SupplierCode == y.SupplierCode &&
            x.SupplierName == y.SupplierName &&
            x.Status == y.Status &&
            x.Notes == y.Notes &&
            x.Total == y.Total &&
            x.Lines.SequenceEqual(y.Lines);

        public int GetHashCode(PurchaseOrderDto order)
        {
            var hash = new HashCode();
            hash.Add(order.SupplierCode);
            hash.Add(order.SupplierName);
            hash.Add(order.Status);
            hash.Add(order.Notes);
            hash.Add(order.Total);
            foreach (var line in order.Lines)
            {
                hash.Add(line);
            }
            return hash.ToHashCode();
        }
    }
}
