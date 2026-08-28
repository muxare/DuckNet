---
name: "source-command-run-aspire"
description: "Run the DuckNet Aspire host (TelemetryCenter + AlarmCenter + DashboardCenter)."
---

# source-command-run-aspire

Use this skill when the user asks to run the migrated source command `run-aspire`.

## Command Template

Run the Step 7 multi-Center demo (team-shared, human-triggered).

1. From the repo root:

```bash
dotnet run --project src/DuckNet.AppHost
```

2. Open the Aspire dashboard URL printed at startup. `telemetry`, `alarm`, and `dashboard` should be healthy.
3. Click the **dashboard** resource URL — Vue UI of `squeaks_by_duck_hour` (TanStack Query polls every 2s). Rebuild from the button, or `POST /dashboard/rebuild`.
4. Optional poison: `POST` Telemetry `/bus/poison`, then `GET` Alarm or Dashboard `/dlq`. Replay with `POST /dlq/{id}/replay?fix=true` or skip with `POST /dlq/{id}/skip`.
5. Optional: stop `alarm` in Aspire, wait for a squeak burst, start it again — `/alarms` on AlarmCenter should fill in from the log.
6. Mixed v1/v2 replay is `dotnet test --filter MixedVersion`, not a live Aspire flag.

This is a smoke demo, not a substitute for `dotnet test`.
