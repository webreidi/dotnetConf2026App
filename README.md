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
| GET | `/api/purchase-orders/duplicates` | Flat list of all matching duplicate orders |
| GET | `/api/purchase-orders/duplicates/groups` | Matching duplicate orders grouped by content |
| GET | `/api/purchase-orders/{id}` | One order with lines |
| POST | `/api/purchase-orders` | Create a draft order |

### Duplicate report export

The WinForms **Generate Duplicate Records Report** button saves
`duplicate-purchase-orders.md`. Its fenced JSON includes every matched group,
every complete order and line, and an explicit `duplicateId`/`canonicalId` pair
for each non-canonical record. It uses the same server-side matching rules as
the preserved flat endpoint. The earliest-created order is chosen as canonical;
UUID text order breaks creation-time ties. This is a review-only choice, not
proof of which record was original, and no records are modified.

Synthetic output:

````markdown
# Duplicate purchase orders report

Matches compare supplier code and name, status, notes, total, and every line item; PO ID, generated order number, and creation timestamp are excluded.
For review, the canonical record is the earliest-created order; ties are broken by ordinal UUID text. This choice is for review only, not proof of which record was original.
These saved drafts have not been sent to suppliers.

```json
{
  "duplicateRecords": 2,
  "duplicateGroups": [
    {
      "canonicalId": "00000000-0000-0000-0000-000000000001",
      "duplicatePairs": [
        {
          "duplicateId": "00000000-0000-0000-0000-000000000002",
          "canonicalId": "00000000-0000-0000-0000-000000000001"
        }
      ],
      "orders": [
        {
          "notice": "Saved draft purchase order for review only. This draft has not been sent to suppliers.",
          "order": {
            "id": "00000000-0000-0000-0000-000000000001",
            "orderNumber": "PO-SYNTHETIC-001",
            "supplierCode": "ACME",
            "supplierName": "Acme Industrial Supply",
            "status": "Draft",
            "notes": "Synthetic example",
            "total": 10.00,
            "createdAt": "2026-11-01T10:00:00+00:00",
            "lines": [
              {
                "lineNumber": 1,
                "sku": "BOLT",
                "description": "Hex bolt",
                "quantity": 1,
                "unitPrice": 10.00,
                "lineTotal": 10.00
              }
            ]
          }
        },
        {
          "notice": "Saved draft purchase order for review only. This draft has not been sent to suppliers.",
          "order": {
            "id": "00000000-0000-0000-0000-000000000002",
            "orderNumber": "PO-SYNTHETIC-002",
            "supplierCode": "ACME",
            "supplierName": "Acme Industrial Supply",
            "status": "Draft",
            "notes": "Synthetic example",
            "total": 10.00,
            "createdAt": "2026-11-02T10:00:00+00:00",
            "lines": [
              {
                "lineNumber": 1,
                "sku": "BOLT",
                "description": "Hex bolt",
                "quantity": 1,
                "unitPrice": 10.00,
                "lineTotal": 10.00
              }
            ]
          }
        }
      ]
    }
  ]
}
```
````

### Submission identity and replay

POST requires one `Idempotency-Key` header containing a non-empty UUID. Keep it
for every retry/replay of one logical submission; generate a new UUID for a
legitimate new order, even when its contents are identical. Missing/invalid keys
return a validation problem (400); a key reused with conflicting content returns
a problem response (409). Successful creation and replay both return 201 with the
same order and `Location`. Existing route contracts and request/response bodies
are unchanged.
There is no authentication or tenant model in this sample: identities are scoped
to the single customer's purchasing database, not to suppliers. A multi-tenant
deployment must add authenticated tenant scope to both lookup and the durable key.

The typed client creates one key outside its retry loop. Callers replaying across
separate calls can supply `submissionId` to `CreateDraftAsync`; retain that UUID
alongside the draft if replay must survive a process restart. The desktop form
retains its UUID after uncertain failures, including manual retries. Success or
**Clear** starts a new submission; after an uncertain result, retry the unchanged
draft before editing or clearing it. The form does not persist unsaved drafts or
their UUIDs across application restarts.

The retained PostgreSQL volume was inspected before implementation. It contained
41 orders and five rows in `purchase_order_submissions`, although that ledger was
absent from the EF model. Its existing `Id` UUID primary key
(`pk_purchase_order_submissions`) is reused, along with `RequestPayload`,
`PurchaseOrderId`, and `ResponseJson`. A unique index on `PurchaseOrderId` and a
restricting foreign key preserve the order/identity association. Order, lines,
canonical request, and response snapshot are inserted by one EF `SaveChangesAsync`
transaction. Concurrent attempts may both reach insertion; PostgreSQL's primary
key chooses the winner and rolls back the losing order and lines. Only that
specific key violation is resolved as a replay; unrelated database errors surface.

Requests are compared using persisted semantics: supplier/SKU case and outer
whitespace are normalized, blank notes equal null, decimals compare numerically,
and line order remains significant. Historical PascalCase payloads with quoted
prices are supported. Replays use the stored response snapshot, even if a supplier
has since changed or become inactive, without repeating simulated preparation.

The original write commits **before** the simulated supplier delay. Cancelling
that delay cannot undo the write. A timeout/cancellation is never treated as proof
of rollback or used to delete a ledger entry. Aspire's production Npgsql execution
strategy wraps the complete create attempt: on a transient retry it first reads
the durable ledger, including when a commit acknowledgement was lost. Request
cancellation still propagates; a later replay resolves any committed result.
There are no real supplier side effects in this sample; these guarantees are for
database creation, not exactly-once delivery to an external system.

### Existing database compatibility

Startup initialization runs inside the provider execution strategy and a
PostgreSQL transaction protected by an advisory lock. Fresh databases receive the
full model. Populated volumes get the ledger if absent and an additive nullable
`ResponseJson` column if missing; an existing NOT NULL response column, all rows,
keys, payload text, and saved responses remain untouched. Repeated initialization
is safe, and failed DDL rolls back. No volumes are reset and historical duplicate
orders are not merged or deleted.

For older ledger rows without response snapshots, replay resolves the associated
saved order, durably freezes the first reconstructed response, and logs a warning:
the original supplier display name, exact response
timestamp precision, or other historical response-only values cannot be recovered.
The inspected five historical ledger rows already have snapshots and retain them.
For orders without a recorded submission identity, neither the original UUID nor
the grouping of historical retries can be reconstructed safely from equal payloads;
such orders are preserved but cannot be retroactively deduplicated.

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
