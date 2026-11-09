# API owner handoff: purchase-order POST retries

## Reproduction

1. Start the Aspire app with AppHost configuration `SupplierSimulation:SlowRequestRate=1` and `SupplierSimulation:SlowLatencyMs=4000` so each POST takes longer than the desktop client's request timeout.
2. In the purchase-order desk, choose a supplier, enter one valid line, and create a draft.
3. The client timeout is 2 seconds per attempt, with up to 3 attempts and 250 ms / 500 ms retry delays. It resends the same POST when an attempt times out; after the last timeout it surfaces a `TimeoutException` (about 6.75 seconds for three timeouts).
4. In the API service, `SaveChangesAsync` currently runs before the simulated supplier-preparation delay. Thus a timed-out attempt may already have persisted a draft, and each replay may persist another. The UI now reports that the submission was not confirmed, retains the entered form, and refreshes the recent-orders list. The GET is unfiltered and the client does not deduplicate its rows, so any returned duplicates remain visible. The endpoint currently lists at most 50 recent orders.

## Relevant locations

- POST and list routes: `src/PurchaseOrders.Api/Endpoints/PurchaseOrderEndpoints.cs`
- Persistence and simulated-delay sequence: `src/PurchaseOrders.Api/Services/PurchaseOrderService.cs`
- Delay and cancellation behavior: `src/PurchaseOrders.Api/Services/SupplierOrderSimulator.cs`
- Timeout/retry settings and POST replay: `src/PurchaseOrders.Client/PurchaseOrdersClientOptions.cs`, `src/PurchaseOrders.Client/PurchaseOrdersApiClient.cs`
- User notification and refreshed list: `src/PurchaseOrders.WinForms/PurchaseOrderForm.cs`
- Simulation defaults/overrides: `src/PurchaseOrders.AppHost/AppHost.cs`

## Acceptance criteria

A timeout followed by a retry or replay of the same logical submission must not create another purchase-order record. Confirm with the API owner how a logical submission is identified (including key lifetime and behavior if the payload changes), and verify persistence/transaction ordering and the intended list ordering. No idempotency identity or ordering policy is assumed in this handoff.