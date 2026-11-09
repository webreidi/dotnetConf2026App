# API owner handoff: purchase-order POST retries

## Original reproduction (before the replay fix)

1. Start the Aspire app with AppHost configuration `SupplierSimulation:SlowRequestRate=1` and `SupplierSimulation:SlowLatencyMs=4000` so each POST takes longer than the desktop client's request timeout.
2. In the purchase-order desk, choose a supplier, enter one valid line, and create a draft.
3. The client timeout is 2 seconds per attempt, with up to 3 attempts and 250 ms / 500 ms retry delays. It resends the same POST when an attempt times out; after the last timeout it surfaces a `TimeoutException` (about 6.75 seconds for three timeouts).
4. In the original API service, `SaveChangesAsync` ran before the simulated supplier-preparation delay. Thus a timed-out attempt could already have persisted a draft, and each replay could persist another. The UI reported that the submission was not confirmed, retained the entered form, and refreshed the recent-orders list. The GET was unfiltered and the client did not deduplicate its rows, so any returned duplicates remained visible. The endpoint listed at most 50 recent orders by default.

## Relevant locations

- POST and list routes: `src/PurchaseOrders.Api/Endpoints/PurchaseOrderEndpoints.cs`
- Persistence and simulated-delay sequence: `src/PurchaseOrders.Api/Services/PurchaseOrderService.cs`
- Delay and cancellation behavior: `src/PurchaseOrders.Api/Services/SupplierOrderSimulator.cs`
- Timeout/retry settings and POST replay: `src/PurchaseOrders.Client/PurchaseOrdersClientOptions.cs`, `src/PurchaseOrders.Client/PurchaseOrdersApiClient.cs`
- User notification and refreshed list: `src/PurchaseOrders.WinForms/PurchaseOrderForm.cs`
- Simulation defaults/overrides: `src/PurchaseOrders.AppHost/AppHost.cs`

## Acceptance criteria

A timeout followed by a retry or replay of the same logical submission must not create another purchase-order record. Confirm with the API owner how a logical submission is identified (including key lifetime and behavior if the payload changes), and verify persistence/transaction ordering and the intended list ordering. No idempotency identity or ordering policy is assumed in this handoff.

## Verified implementation and retained-state findings

The original code did commit before the cancellable simulator delay. That delay
has no supplier/network side effects. Cancellation therefore did not imply that
the draft was absent. The list sorts by descending creation time; its default
limit remains 50 and no ordering change is made.

Inspection of the actual retained PostgreSQL volume found 52 orders and seven
existing submission identities, including payloads and response snapshots in
`purchase_order_submissions`, which the old EF model did not include. Neither
the report nor the old model described that retained schema completely.

The API now requires a UUID `Idempotency-Key` and uses the existing ledger's
global identity scope. Order creation and its ledger entry commit atomically,
within the configured retrying execution strategy. Replays return the original
saved response and Location with 201; conflicting data returns 409. The client
keeps its key across automatic retries and the form retains it for an unchanged,
unconfirmed submission. See [retained database compatibility](../README.md#retained-database-compatibility)
for upgrade guarantees and historical-response recovery limits.