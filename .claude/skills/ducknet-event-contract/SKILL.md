---
name: ducknet-event-contract
description: Version an event contract and add an upcaster so mixed vN/vN+1 logs replay in every Center. Use when adding a field to Squeaked or AlarmRaised, introducing SqueakedV3, changing PayloadJson, bumping EventEnvelope.Version, or when a consumer throws "must be upcast before parse".
argument-hint: "[event-type] [from-version] [to-version]"
allowed-tools: Read, Edit, Write, Grep, Glob, Bash(dotnet *)
paths:
  - "src/DuckNet.Contracts/**"
  - "src/DuckNet.EventBus/**"
  - "tests/**/*Upcaster*"
  - "tests/**/*MixedVersion*"
---

# ducknet-event-contract

Contracts are immutable shapes. Behavior lives in upcasters. Handlers parse **only** the current version.

## Invariants

- `DuckNet.Contracts` — payload records and `EventEnvelope` only. No DB, no HTTP, no upcast logic.
- New facts get a new `Version`. Old rows in `event_log` stay as they were written.
- Upcast rewrites `Version` + `PayloadJson`. **`EventId` does not change** (inbox still dedups).
- Default for a new field is explicit and testable (Squeaked v1→v2: `VolumeDb = 0`, unknown — not an estimate).
- Every consumer upcasts **before** `Parse`. `SqueakedEnvelope.Parse` rejects stale versions on purpose.
- A contract change deploys **all** Centers (`deploy-center.yml` fans out on `src/DuckNet.Contracts/**` and `src/DuckNet.EventBus/**`).

## Checklist

1. Freeze the old payload type (`SqueakedV1`) if it is not already frozen. Do not mutate it.
2. Add or extend the current type (`Squeaked` = v2 today). Bump `Version` constant.
3. `Create` emits the new version. Keep `CreateV{old}` for mixed-log tests only.
4. Implement `IEventUpcaster`: `CanUpcast(type, oldVersion)` → rewrite payload + bump `Version`.
5. Register it in `EventUpcasterPipeline` (chain until no match; must increase `Version` each hop).
6. Handlers: `_upcasters.Upcast(envelope)` then `Parse`. No `if (version == 1)` in Alarm/Dashboard/kernel handlers.
7. Read-model columns: nullable migration for existing SQLite files (`EnsureVolumeColumn` pattern).
8. Tests: unit defaults + EventId preserved; mixed v1/v2 log replay in **each** consumer Center; Parse-without-upcast throws.

## Verify

```bash
dotnet test --filter "FullyQualifiedName~Upcaster|FullyQualifiedName~MixedVersion"
```
