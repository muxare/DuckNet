# DuckNet

Toy domain, real distributed architecture. Smart rubber ducks emit facts; Centers react via events only.

## Non-negotiable rules

1. **No Center-to-Center calls.** Integration is events only.
2. **No shared database.** Each Center owns its schema.
3. **Events are past facts**, not commands (`Squeaked`, not `SqueakTheDuck`).
4. **Transport is hostile** (from Step 1): at-least-once, unordered across keys.
5. **Every step stays runnable.** Tag and merge on completion.

## Git workflow

- One branch per step: `step-0`, `step-1`, …
- Implement on the branch; merge to `main` only when acceptance criteria pass.
- Tag on merge: `git tag step-N`
- Commit format: `feat(step-N): description`

## Layout (Step 0)

```
src/DuckNet.Kernel/     # single-process kernel until Step 4
tests/                  # unit + integration tests
.github/workflows/      # ci.yml, claude-review.yml
```

## Build & test

```bash
dotnet build
dotnet test
dotnet run --project src/DuckNet.Kernel -- --run-demo --seconds 5
```

## Step progress

| Step | Status | Branch |
|------|--------|--------|
| 0 | complete | `step-0` → `main` |
| 1 | pending | — |

See [ImplementationPlan.md](./ImplementationPlan.md) for full roadmap.
