---
description: Run the DuckNet Aspire host (TelemetryCenter + AlarmCenter).
argument-hint: ""
allowed-tools: Bash(dotnet *)
disable-model-invocation: true
---

Run the Step 4 multi-Center demo (team-shared, human-triggered).

1. From the repo root:

```bash
dotnet run --project src/DuckNet.AppHost
```

2. Open the Aspire dashboard URL printed at startup. Both `telemetry` and `alarm` should be healthy.
3. Optional: stop `alarm` in the dashboard, wait for a squeak burst, start it again — `/alarms` on AlarmCenter should fill in from the log.

This is a smoke demo, not a substitute for `dotnet test`.
