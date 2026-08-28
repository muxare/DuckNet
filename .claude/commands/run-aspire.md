---
description: Run the DuckNet Aspire host (TelemetryCenter + AlarmCenter + DashboardCenter).
argument-hint: ""
allowed-tools: Bash(dotnet *)
disable-model-invocation: true
---

Run the Step 5 multi-Center demo (team-shared, human-triggered).

1. From the repo root:

```bash
dotnet run --project src/DuckNet.AppHost
```

2. Open the Aspire dashboard URL printed at startup. `telemetry`, `alarm`, and `dashboard` should be healthy.
3. `GET /dashboard/summary` on DashboardCenter. Optional: `POST /dashboard/rebuild` — the hour buckets refill from the log.
4. Optional: stop `alarm` in the dashboard, wait for a squeak burst, start it again — `/alarms` on AlarmCenter should fill in from the log.

This is a smoke demo, not a substitute for `dotnet test`.
