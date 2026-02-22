# Azure Cost Spike Explainer (MVP)

Day 1 to Day 6 are implemented:

- `backend`: .NET 8 Web API with JWT auth, EF Core, and health endpoints
- `worker`: .NET 8 Worker Service with cost ingestion, spike detection, and waste finding scans
- `frontend`: Angular 17 standalone app with `/connect` and `/dashboard`
- `shared/AzCostPilot.Data`: shared entities + `AppDbContext` + EF migrations

## Current APIs

- `POST /auth/register`
- `POST /auth/login`
- `GET /health`
- `GET /health/db`
- `POST /connect/azure` (JWT required, validates Azure credentials and lists subscriptions)
- `GET /connections/azure` (JWT required)
- `GET /cost/latest-7-days` (JWT required, returns totals + per-resource daily costs)
- `GET /dashboard/summary` (JWT required, returns yesterday/today totals, spike flag, top cause resource, suggestion)
- `GET /dashboard/history` (JWT required, returns last 10 cost events)
- `GET /dashboard/waste-findings` (JWT required, returns easy-savings findings)

## Local run

1. Start PostgreSQL (example with Docker):
   - `docker compose up -d postgres`
2. Run API:
   - `dotnet run --project backend/AzCostPilot.Api.csproj`
3. Run worker:
   - `dotnet run --project worker/AzCostPilot.Worker.csproj`
4. Run frontend:
   - `cd frontend`
   - `npm install`
   - `npm start`

Frontend uses API base URL `http://localhost:5168`.

## Database schema

Initial migration creates:

- `users`
- `azure_connections`
- `subscriptions`
- `daily_cost_resource`
- `cost_events`
- `waste_findings`
