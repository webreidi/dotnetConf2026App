using Microsoft.EntityFrameworkCore;
using PurchaseOrders.Api.Services;

namespace PurchaseOrders.Api.Data;

public static class PurchasingDatabaseInitializer
{
    public static Task InitializeAsync(PurchasingDbContext db, CancellationToken cancellationToken = default) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(7349271058261)", cancellationToken);

            var exists = await db.Database.SqlQueryRaw<bool>(
                """SELECT to_regclass('public.purchase_orders') IS NOT NULL AS "Value" """)
                .SingleAsync(cancellationToken);
            if (!exists)
            {
                await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript(), cancellationToken);
            }
            else
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS purchase_order_submissions (
                        "Id" uuid NOT NULL CONSTRAINT pk_purchase_order_submissions PRIMARY KEY,
                        "RequestPayload" text NOT NULL,
                        "PurchaseOrderId" uuid NOT NULL
                            REFERENCES purchase_orders ("Id") ON DELETE RESTRICT,
                        "ResponseJson" text
                    );
                    ALTER TABLE purchase_order_submissions ADD COLUMN IF NOT EXISTS "ResponseJson" text;
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_purchase_order_submissions_PurchaseOrderId"
                        ON purchase_order_submissions ("PurchaseOrderId");
                    """, cancellationToken);

                var missingResponses = await db.Database.SqlQueryRaw<Guid>("""
                    SELECT "PurchaseOrderId" AS "Value" FROM purchase_order_submissions
                    WHERE "ResponseJson" IS NULL
                    """).ToListAsync(cancellationToken);
                foreach (var orderId in missingResponses)
                {
                    var order = await db.PurchaseOrders.Include(o => o.Supplier).Include(o => o.Lines)
                        .SingleAsync(o => o.Id == orderId, cancellationToken);
                    var response = SubmissionSerialization.SerializeResponse(order.ToDto());
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE purchase_order_submissions SET "ResponseJson" = {response}
                        WHERE "PurchaseOrderId" = {orderId} AND "ResponseJson" IS NULL
                        """, cancellationToken);
                }

                await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE purchase_order_submissions ALTER COLUMN "ResponseJson" SET NOT NULL;
                    """, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
}
