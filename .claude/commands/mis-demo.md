---
description: Run the DuckNet mis-demo with inbox off so duplicate deliveries inflate counts.
argument-hint: "[seconds]"
allowed-tools: Bash(dotnet *)
disable-model-invocation: true
---

Run the kernel mis-demo (team-shared, human-triggered). Inbox is disabled on purpose.

1. Seconds = `$ARGUMENTS` when it is a positive integer; otherwise 5.
2. From the repo root:

```bash
dotnet run --project src/DuckNet.Kernel -- --mis-demo --seconds <seconds>
```

3. Print the totals. Counted should exceed Published when duplicates were injected. This is a teaching demo, not a substitute for `dotnet test`.
