# API owner handoff: purchase-order submission timeout

## Reproduction and observed behavior

1. Start the Aspire app and create a draft purchase order. For a deterministic slow-path repro, configure the AppHost's `SupplierSimulation:SlowRequestRate` to `1` and keep `SupplierSimulation:SlowLatencyMs` above the client timeout.
2. The client timeout defaults to 2 seconds and it retries a request up to 3 times, with a delay between attempts. The simulated slow path defaults to 4 seconds and is selected for 30% of requests.
3. In the current code path, after all attempts time out, the client throws `TimeoutException`. The form previously displayed only a status-bar error and did not refresh the order list.
4. With the UI change, the form clearly warns that the order may still have been saved, then refreshes the recent-order list from `GET /api/purchase-orders`. The list shows the server's returned records without client-side duplicate filtering (the API list defaults to the most recent 50).

These are code/configuration observations; the scenario has not been runtime-reproduced as part of this handoff. A timeout does not establish whether the server persisted the request. The API service currently calls `SaveChangesAsync` before simulated supplier preparation, so inspect and confirm the actual persistence/commit boundary and cancellation behavior before selecting a server-side remedy.

## Relevant locations

- UI submission and refreshed list: `src/PurchaseOrders.WinForms/PurchaseOrderForm.cs` (`CreateDraftAsync`, `LoadRecentOrdersAsync`)
- Client timeout/retry and POST: `src/PurchaseOrders.Client/PurchaseOrdersApiClient.cs` (`CreateDraftAsync`, `SendAsync`)
- Timeout/retry defaults: `src/PurchaseOrders.Client/PurchaseOrdersClientOptions.cs`
- Create endpoint: `src/PurchaseOrders.Api/Endpoints/PurchaseOrderEndpoints.cs` (`POST /api/purchase-orders`)
- Persistence and simulated preparation: `src/PurchaseOrders.Api/Services/PurchaseOrderService.cs`
- Simulated slow path/defaults: `src/PurchaseOrders.Api/Services/SupplierOrderSimulator.cs`, `src/PurchaseOrders.AppHost/AppHost.cs`

## API acceptance criteria

A timeout followed by a retry or replay of the same logical submission must not create another purchase-order record. Confirm with the API owner how a logical submission is identified and how the persistence/commit boundary interacts with retries; this handoff does not assume duplicate identity rules or commit ordering beyond the call ordering visible in the current service code.
