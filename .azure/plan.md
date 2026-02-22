# Azure Cost Spike Explainer - Build Plan

## Status
- Current phase: Execution (Day 6 completed)
- Validation status: Day 1-Day 6 build and runtime validation complete

## Scope
- Build MVP in daily increments (Day 1 to Day 7)
- Current execution target: Day 7 next

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

## Day 2 Acceptance
- `POST /connect/azure` validates service principal credentials using Azure Management token flow
- Subscriptions are fetched and stored in `subscriptions`
- Connect UI shows connected status and subscription count

## Day 3 Acceptance
- Worker ingests last 7 days of daily cost by resource into `daily_cost_resource`
- API endpoint `GET /cost/latest-7-days` returns totals and per-resource costs for authenticated user

## Day 4 Acceptance
- Worker computes daily spike baseline and cause extraction, then writes `cost_events`
- Spike rule implemented: `today > baseline * 1.5` and `difference > 5`
- API endpoint `GET /dashboard/summary` returns yesterday/today totals, spike flag, top cause resource, and suggestion text

## Day 5 Acceptance
- Dashboard UI shows yesterday/today totals, baseline, difference, spike badge, main cause resource, and suggestion text
- Dashboard includes "View in Azure" link built from resource ID
- Dashboard includes optional history list from last 10 `cost_events`

## Day 6 Acceptance
- Worker scans for `unattached_disk`, `unused_public_ip`, and `stopped_vm` findings via Azure Resource Graph
- Findings are stored in `waste_findings` with estimated monthly savings (recent-cost-based or heuristic fallback)
- Dashboard includes an "Easy Savings Found" section with Azure portal links

## Additional Enhancement
- `POST /connect/azure` now triggers immediate 30-day backfill using shared `CostSyncService`
- Shared cost sync logic is reused by both API-triggered backfill and scheduled worker runs
