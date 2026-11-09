# Purchase Orders (Aspire sample)

A small .NET 11 / Aspire 13.6.2 app for entering **draft** purchase orders. Nothing is sent anywhere: there are no supplier calls, payments, or emails. Supplier latency is simulated in-process.

## Projects

| Project | Purpose |
|---|---|
| `src/PurchaseOrders.AppHost` | Aspire AppHost: PostgreSQL + `purchasingdb`, the API, and the WinForms client |
| `src/PurchaseOrders.ServiceDefaults` | OpenTelemetry, health checks, service discovery, resilience |
| `src/PurchaseOrders.Api` | ASP.NET Core minimal API + EF Core (Npgsql) |
| `src/PurchaseOrders.Contracts` | Shared request/response DTOs |
| `src/PurchaseOrders.Client` | Typed HTTP client used by the desktop app |
| `src/PurchaseOrders.WinForms` | Purchase-order entry screen (Windows only) |
| `tests/PurchaseOrders.UnitTests` | Unit tests for validation, the domain, and the client |
| `tests/PurchaseOrders.IntegrationTests` | End-to-end tests via `Aspire.Hosting.Testing` (real Postgres container) |

## Prerequisites

- .NET 11 SDK
- Aspire CLI (`dotnet tool install -g Aspire.Cli`)
- Docker Desktop (or Podman) running, for PostgreSQL
- Windows, for the WinForms client

## Run

```powershell
aspire start --apphost src/PurchaseOrders.AppHost/PurchaseOrders.AppHost.csproj
```

The dashboard shows `postgres`, `purchasingdb`, `api`, and `purchase-order-desk` (the WinForms window).

## Observability

The API exports ASP.NET Core, HTTP client, and .NET runtime telemetry through Aspire's OpenTelemetry defaults. Its domain telemetry adds the `purchase_orders.draft_creation_attempts` counter (tagged by outcome) and `purchase_orders.supplier_preparation.duration` histogram (tagged by outcome and whether the simulated preparation was slow), with spans for draft creation and supplier preparation. View these signals in the Aspire dashboard while the AppHost is running. Health and liveness endpoints are available in Development at `/health` and `/alive`.

## API

| Method | Route | Description |
|---|---|---|
| GET | `/api/suppliers` | Active suppliers |
| GET | `/api/purchase-orders?supplierCode=&take=` | Recent draft orders |
| GET | `/api/purchase-orders/{id}` | One order with lines |
| POST | `/api/purchase-orders` | Create a draft order |

### Submission identity and replay

POST requires exactly one `Idempotency-Key` header containing a nonempty UUID.
Keep that UUID for every timeout, transport retry, or later replay of one logical
submission. Use a new UUID for an intentional new order, even with identical data.
Missing/invalid identities return the existing validation-problem format (400);
reusing an identity with different order data returns Problem Details (409).
Successful creation and replay both return 201 with the same order and Location.
Deploy the updated client with the API and stop old API writers before rollout;
keyless requests cannot be safely identified from payload similarity.
Payload comparison applies the domain's trimming/case normalization, treats empty
notes as null and prices numerically, and preserves line order.

The typed client generates one key per call, outside its retry loop. Callers that
need replay across calls/restarts must retain their key and use the explicit-key
overload. The desktop form retains its key after an unconfirmed outcome while
the data is unchanged; clearing, editing, or a confirmed success starts a new
submission. Its pending key is in memory only, not recovered after process exit.

PostgreSQL's `purchase_order_submissions` primary key (`Id`) is the durable
uniqueness boundary, with a unique `PurchaseOrderId` index and a restrictive
foreign key to the order. The ledger row, canonical payload, original response,
order, and lines commit in one transaction. A transaction-scoped advisory lock
serializes the identity's check/insert; the primary key remains the storage
constraint. Transactions run inside the production Npgsql execution strategy,
clearing tracked inserts before each retry and reading the ledger again after
an ambiguous commit. Cancellation never removes committed identities.
Simulated supplier preparation still occurs after commit; a replay skips that
in-process delay because it has no external side effect.

This sample has no tenant/customer or authentication model: identities are scoped
to the entire purchasing database, not to suppliers (which are vendors).
Multi-tenant deployment requires authenticated tenant scope in both the ledger
key and locking/lookup logic before sharing a database. Keep ledger entries for
as long as replay is supported; there is no expiry or deletion policy.

### Retained database compatibility

Startup uses a transactional, advisory-locked initializer, not `EnsureCreated`
as an upgrade mechanism. Fresh databases receive the full EF schema. Existing
orders/lines/suppliers remain intact; an absent submission ledger is added, and
an older ledger without `ResponseJson` gains the column and a backfilled response
before the column becomes required. Initialization is repeatable and rollback
leaves the old schema/data intact if an upgrade fails.
Existing duplicate ledger references or inconsistent rows fail startup rather
than being silently deleted or rewritten; investigate those rows before retrying.

The inspected retained volume already contained a ledger with UUID `Id`,
`RequestPayload`, `PurchaseOrderId`, and required `ResponseJson`, despite its absence
from the old EF model. Existing IDs, quoted-price payloads, and PascalCase or
camelCase saved responses are preserved and remain replayable.
When an older row has no response snapshot, only the current saved order and
supplier can reconstruct one: the historical supplier name, original JSON
formatting, and timestamp precision lost by PostgreSQL cannot be recovered.
The reconstructed snapshot is then frozen for subsequent replays.
Orders without any historical submission identity cannot safely be associated
with a retry by matching their contents; they are preserved, not deduplicated
or assigned invented identities.

## Configuration (AppHost)

| Key | Default | Meaning |
|---|---|---|
| `SupplierSimulation:SlowRequestRate` | `0.3` | Fraction of draft creations with simulated slow supplier preparation |
| `SupplierSimulation:SlowLatencyMs` | `4000` | Latency of a slow preparation |
| `DesktopClient:Enabled` | `true` | Launch the WinForms client |
| `Postgres:UseDataVolume` | `true` | Persist Postgres data in a named volume |

## Test

```powershell
dotnet test tests/PurchaseOrders.UnitTests
dotnet test tests/PurchaseOrders.IntegrationTests   # requires Docker
```

Integration regressions use real PostgreSQL with retries enabled, including
concurrent replay, payload conflict, distinct intentional submissions, lost
commit acknowledgements, post-commit cancellation/timeout, and populated legacy
schema upgrades (repeat initialization, preserved snapshots, and DDL rollback).
Tests use disposable databases/containers, never the retained application volume.
