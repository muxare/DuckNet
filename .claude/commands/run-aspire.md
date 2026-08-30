---
description: Run the DuckNet Aspire host (TelemetryCenter + AlarmCenter + DashboardCenter + BillingCenter).
argument-hint: ""
allowed-tools: Bash(dotnet *)
disable-model-invocation: true
---

Run the Step 10 multi-Center demo (team-shared, human-triggered).

1. From the repo root:

```bash
dotnet run --project src/DuckNet.AppHost
```

2. Open the Aspire dashboard URL printed at startup. `telemetry`, `alarm`, `dashboard`, and `billing` should be healthy.
3. Open **Traces**. Names are `{resource}: {span}` (`alarm: handle.Squeaked`). Filter `handle.Squeaked` — not `DuckNet.*`. Click a row: `simulate.squeak` / `ingest.squeak` → `append.log` → both Centers' `handle.Squeaked` share one `TraceId`. A duplicate is a second handle span on that trace tagged `ducknet.duplicate`. HTTP `GET`s are usually separate traces.
4. Click the **dashboard** resource URL — Vue UI of `squeaks_by_duck_hour` plus per-shard queue/lag cards (TanStack Query polls every 2s). Rebuild from the button, or `POST /dashboard/rebuild`.
5. LoudDuck (`duck-1`) is on. `GET /metrics` on alarm or dashboard shows per-shard lag. `SHARD_COUNT=1` on a Center re-starves quiet keys.
6. **Saga:** `GET` billing `/sagas`. Fast path: `POST` alarm `/alarms/duck-1/resolve` after a raise → saga `Released`. Slow path: leave it; `SAGA_TIMEOUT_SECONDS=15` → `Expired` + `FeeReleased` reason `Timeout`.
7. Optional poison: `POST` Telemetry `/bus/poison`, then `GET` Alarm or Dashboard `/dlq`. Replay with `POST /dlq/{id}/replay?fix=true` or skip with `POST /dlq/{id}/skip`.
8. Optional: stop `alarm` in Aspire, wait for a squeak burst, start it again — `/alarms` on AlarmCenter should fill in from the log.
9. Mixed v1/v2 replay is `dotnet test --filter MixedVersion`, not a live Aspire flag.
10. Kernel before/after: `--hot-demo --shard-count 1` vs `--shard-count 3`.

This is a smoke demo, not a substitute for `dotnet test`.
