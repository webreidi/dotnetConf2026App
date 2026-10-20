using System.Security.Cryptography;

namespace PurchaseOrders.Api.Domain;

public static class OrderNumberGenerator
{
    public static string Create(DateTimeOffset now) =>
        $"PO-{now.UtcDateTime:yyyyMMdd}-{RandomNumberGenerator.GetHexString(6)}";
}
