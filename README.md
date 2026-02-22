# Azure Cost Spike Explainer (MVP)

Day 1 scaffold is implemented:

- `backend`: .NET 8 Web API with JWT auth, EF Core, and health endpoints
- `worker`: .NET 8 Worker Service with DB heartbeat wiring
- `frontend`: Angular 17 standalone app with `/connect` and `/dashboard`
- `shared/AzCostPilot.Data`: shared entities + `AppDbContext` + EF migrations

## Day 1 APIs

- `POST /auth/register`
- `POST /auth/login`
- `GET /health`
- `GET /health/db`
- `POST /connections/azure` (JWT required)
- `GET /connections/azure` (JWT required)

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
