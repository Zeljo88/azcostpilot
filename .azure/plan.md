# Azure Cost Spike Explainer - Build Plan

## Status
- Current phase: Execution (Day 1 completed)
- Validation status: Partial (compile/build complete, DB runtime validation pending local PostgreSQL)

## Scope
- Build MVP in daily increments (Day 1 to Day 7)
- Current execution target: Day 1 only

## Day Plan
1. Day 1: Scaffold backend, worker, frontend, Postgres schema/migrations, auth mode, health endpoints, Angular routes
2. Day 2: Azure service principal connect + subscription listing
3. Day 3: Cost ingestion for last 7 days by resource
4. Day 4: Spike detection and cause extraction
5. Day 5: Dashboard explanation-first UI
6. Day 6: Waste checks
7. Day 7: Notifications, guardrails, deployment

## Architecture Choices
- Backend: .NET 8 Web API
- Worker: .NET 8 Worker Service
- Frontend: Angular 17 standalone
- Database: PostgreSQL with Entity Framework Core
- Auth for MVP: JWT

## Day 1 Acceptance
- Frontend routes `/connect` and `/dashboard` render
- API health endpoints return success
- Database contains `users` and `azure_connections` records via API flow
