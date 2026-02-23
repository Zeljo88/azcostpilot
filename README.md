# Azure Cost Spike Explainer (MVP)

Day 1 to Day 7 are implemented:

- `backend`: .NET 8 Web API with JWT auth, EF Core, and health endpoints
- `worker`: .NET 8 Worker Service with cost ingestion, spike detection, and waste finding scans
- `frontend`: Angular 17 standalone app with `/connect` and `/dashboard`
- `shared/AzCostPilot.Data`: shared entities + `AppDbContext` + EF migrations

## Current APIs

- `POST /auth/register`
- `POST /auth/login`
- `GET /health`
- `GET /health/db`
- `POST /connect/azure` (JWT required, validates Azure credentials, stores subscriptions, and runs immediate 30-day backfill)
- `GET /connections/azure` (JWT required)
- `GET /cost/latest-7-days` (JWT required, returns totals + per-resource daily costs)
- `GET /dashboard/summary` (JWT required, returns yesterday/today totals, spike flag, top cause resource, suggestion)
- `GET /dashboard/history` (JWT required, returns last 7 days of unusual events)
- `GET /dashboard/waste-findings` (JWT required, returns easy-savings findings)
- `POST /dev/seed/cost-scenarios` (dev-only, JWT required, seeds synthetic scenarios)

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

## Email notifications (Day 7)

Worker can send spike notifications using SMTP. Configure `worker/appsettings.Development.json`:

```json
"Notifications": {
  "Enabled": true,
  "SmtpHost": "smtp.your-provider.com",
  "SmtpPort": 587,
  "UseSsl": true,
  "Username": "smtp-user",
  "Password": "smtp-password",
  "FromEmail": "alerts@yourdomain.com"
}
```

When a spike is detected for the latest complete billing day, worker sends a short email to the user email used at registration.

## Deployment prep (Day 7)

- `backend/Dockerfile`: API container image
- `worker/Dockerfile`: worker container image

Recommended Azure target:

1. Azure Database for PostgreSQL Flexible Server
2. API on Azure Container Apps (or App Service)
3. Worker on Azure Container Apps Job/Worker
4. Frontend on Static Web Apps or App Service

## Database schema

Initial migration creates:

- `users`
- `azure_connections`
- `subscriptions`
- `daily_cost_resource`
- `cost_events`
- `waste_findings`
