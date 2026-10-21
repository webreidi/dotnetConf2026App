# Purchase Orders (Aspire sample)

A small .NET 10 / Aspire app for entering **draft** purchase orders. Nothing is sent anywhere: there are no supplier calls, payments, or emails. Supplier latency is simulated in-process.

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

- .NET 10 SDK
- Aspire CLI (`dotnet tool install -g Aspire.Cli`)
- Docker Desktop (or Podman) running, for PostgreSQL
- Windows, for the WinForms client

## Run

```powershell
aspire start --apphost src/PurchaseOrders.AppHost/PurchaseOrders.AppHost.csproj
```

The dashboard shows `postgres`, `purchasingdb`, `api`, and `purchase-order-desk` (the WinForms window).

## API

| Method | Route | Description |
|---|---|---|
| GET | `/api/suppliers` | Active suppliers |
| GET | `/api/purchase-orders?supplierCode=&take=` | Recent draft orders |
| GET | `/api/purchase-orders/{id}` | One order with lines |
| POST | `/api/purchase-orders` | Create a draft order |

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
