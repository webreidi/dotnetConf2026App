using System.Security.Cryptography;

namespace PurchaseOrders.Api.Domain;

public static class OrderNumberGenerator
{
    // 16 hex chars = 64 random bits; "PO-yyyyMMdd-" + 16 = 28 chars, within the 32-char column.
    public const int SuffixLength = 16;

    public static string Create(DateTimeOffset now) =>
        $"PO-{now.UtcDateTime:yyyyMMdd}-{RandomNumberGenerator.GetHexString(SuffixLength)}";
}
