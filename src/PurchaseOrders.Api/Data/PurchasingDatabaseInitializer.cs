using Microsoft.EntityFrameworkCore;

namespace PurchaseOrders.Api.Data;

public static class PurchasingDatabaseInitializer
{
    public static async Task InitializeAsync(PurchasingDbContext db, CancellationToken cancellationToken = default)
    {
        // Aspire enables Npgsql retries. The entire DDL transaction must be retried, not its statements.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(726387190451);", cancellationToken);

            await db.Database.EnsureCreatedAsync(cancellationToken);
            // EnsureCreated does not add tables to a populated database. Retain the deployed ledger
            // and its keys verbatim; older installations may not yet have a response snapshot.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS purchase_order_submissions (
                    "Id" uuid NOT NULL,
                    "RequestPayload" text NOT NULL,
                    "PurchaseOrderId" uuid NOT NULL,
                    "ResponseJson" text NULL,
                    CONSTRAINT pk_purchase_order_submissions PRIMARY KEY ("Id"),
                    CONSTRAINT purchase_order_submissions_purchase_order_id_fkey
                        FOREIGN KEY ("PurchaseOrderId") REFERENCES purchase_orders ("Id") ON DELETE RESTRICT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_purchase_order_submissions_PurchaseOrderId"
                    ON purchase_order_submissions ("PurchaseOrderId");
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE purchase_order_submissions ADD COLUMN IF NOT EXISTS "ResponseJson" text NULL;
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
