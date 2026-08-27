# As-built architecture

One file per **completed** step. These diagrams match the code on that step’s branch, not the future target.

Target roadmap (all steps, high-level): [DuckNetArchitectureSteps.html](../../DuckNetArchitectureSteps.html).

| Step | File | Punchline |
|------|------|-----------|
| 0 | [step-0.md](./step-0.md) | One producer, one consumer, counts match |
| 1 | [step-1.md](./step-1.md) | Forced duplicates + inbox → counts still match |
| 2 | [step-2.md](./step-2.md) | Shuffle + per-key sequencer → order and counts still match |
| 3 | [step-3.md](./step-3.md) | Durable log + outbox → kill/restart, no double-count |
| 4 | [step-4.md](./step-4.md) | Second Center, own DB; catch-up from the log |
| 5 | [step-5.md](./step-5.md) | Disposable read model; delete + rebuild from the log |

After each later step: add `step-N.md` here (architecture + execution Mermaid) per [CLAUDE.md](../../CLAUDE.md).
