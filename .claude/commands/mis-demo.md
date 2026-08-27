---
description: Run the DuckNet mis-demo with inbox and sequencer off so duplicates inflate counts and shuffle is visible.
argument-hint: "[seconds]"
allowed-tools: Bash(dotnet *)
disable-model-invocation: true
---

Run the kernel mis-demo (team-shared, human-triggered). Inbox and sequencer are disabled on purpose.

1. Seconds = `$ARGUMENTS` when it is a positive integer; otherwise 5.
2. From the repo root:

```bash
dotnet run --project src/DuckNet.Kernel -- --mis-demo --reset-db --seconds <seconds>
```

3. Print the totals. Counted should exceed Published when duplicates were injected; Out of order is usually greater than 0 with shuffle on. This is a teaching demo, not a substitute for `dotnet test`.
