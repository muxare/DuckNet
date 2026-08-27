---
description: Run the DuckNet kernel demo and print per-duck squeak counts.
argument-hint: "[seconds]"
allowed-tools: Bash(dotnet *)
disable-model-invocation: true
---

Run the kernel demo (team-shared, human-triggered).

1. Seconds = `$ARGUMENTS` when it is a positive integer; otherwise 5.
2. From the repo root:

```bash
dotnet run --project src/DuckNet.Kernel -- --reset-db --seconds <seconds>
```

3. Print the totals. This is a smoke demo, not a substitute for `dotnet test`.
