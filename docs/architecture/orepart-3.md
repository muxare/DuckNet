# OrePart-3 — versioned health + dual-run rebuild

**Not a numbered DuckNet step.** Lab slice after [orepart-2](./orepart-2.md). Closes industry-mapping **G3** (versioned scoring, not a trained model) and the event-backbone half of replay tooling.

**Punchline:** the product promise is “a model,” but the lab proves **versioned scoring + shadow + cutover**. Live queries keep hitting v1 until an explicit cutover.

## Delta vs [orepart-2](./orepart-2.md)

| | |
|--|--|
| **Added** | `AssetHealthPredicted` v2 `ModelVersion` (default `health-v1`), frozen `AssetHealthPredictedV1` + upcaster, `HealthScorer` v2 weights (0.60 / 0.25 / 0.15 vibration / temp / hours), `HEALTH_MODEL` / `HEALTH_SHADOW`, Dashboard `asset_health_shadow`, `POST /dashboard/cutover?to=` |
| **Changed** | Alarm may emit a **shadow** `AssetHealthPredicted` for the other model. Shadow never raises a second `HealthAlertRaised`. Dashboard routes live vs shadow by `health_model` meta. |
| **Unchanged** | Duck path. Inbox / sequencer / outbox. `POST /dashboard/rebuild` still **truncates and replays** (Step 5 path) so existing rebuild tests pass. |

**Plan divergence:** the roadmap sketched side-table rebuild (`?to=fleet-v2` then swap names). As-built dual-run is **health-only**: shadow predictions land in `asset_health_shadow` while live queries read `asset_health_latest`; cutover copies matching shadow rows in one transaction. Rebuild `?to=` is accepted on the route but does not build a second full projection.

No training loop. Weights are constants in `HealthScorer`.

## Wire types

Upcast does **not** mint a new `EventId`. Handlers parse v2 only.

```text
AssetHealthPredicted v1    assetId, score, predictedFailureAt, recommendedService,
                           vibrationMmS, temperatureC, engineHours
AssetHealthPredicted v2    …same…, modelVersion   // default health-v1 on upcast

HealthModels.V1 = "health-v1"
HealthModels.V2 = "health-v2"
```

Config:

```text
HEALTH_MODEL=v1|v2     which version may raise / resolve alerts (default v1)
HEALTH_SHADOW=true     also emit the other version; no second HealthAlertRaised
```

Aspire Alarm sets `HEALTH_SHADOW=true`.

## Architecture

Shadow scoring is an Alarm **outbox fact**, not a Dashboard HTTP call. Billing still keys sagas on `HealthAlertRaised.EventId`; a second prediction with a different `ModelVersion` does not open a second hold.

```mermaid
flowchart TB
  subgraph Aspire["DuckNet.AppHost"]
    subgraph TC["TelemetryCenter — own SQLite"]
      TDB[("event_log mixed predicted v1+v2")]
      BUSAPI["GET/POST /bus/events"]
      TDB --> BUSAPI
    end

    subgraph Transport["IEventBus"]
      HTTP["HttpLogTailFeeder"]
      MEM["hostile bus"]
      BUSAPI --> HTTP --> MEM
    end

    subgraph AC["AlarmCenter — own SQLite"]
      UP["AssetHealthPredictedV1ToV2Upcaster"]
      V1["HealthScorer v1 live"]
      V2["HealthScorer v2 shadow"]
      ADB[("alarm.db")]
      MEM --> UP
      UP --> V1 --> ADB
      UP --> V2
      V1 -->|"HealthAlertRaised only from live model"| BUSAPI
      V2 -->|"AssetHealthPredicted ModelVersion=health-v2 only"| BUSAPI
    end

    subgraph DC["DashboardCenter — own SQLite"]
      LIVE[("asset_health_latest")]
      SHADOW[("asset_health_shadow")]
      CUT["POST /dashboard/cutover"]
      MEM --> LIVE
      MEM --> SHADOW
      CUT -->|"copy shadow → latest in one tx"| LIVE
    end

    subgraph BC["BillingCenter"]
      SAGA["saga on HealthAlertRaised only"]
      MEM --> SAGA
    end
  end

  V2 -.->|"does not raise"| SAGA
  AC -.->|"never calls"| DC
```

## Execution

```mermaid
sequenceDiagram
  participant Tel as event_log
  participant Alarm as AlarmCenter
  participant Dash as DashboardCenter
  participant Billing as BillingCenter

  Tel->>Alarm: SensorReadingReported
  Alarm->>Alarm: score live HEALTH_MODEL
  Alarm->>Tel: AssetHealthPredicted modelVersion=health-v1
  alt score >= 0.70 and live model
    Alarm->>Tel: HealthAlertRaised
    Tel->>Billing: open saga
  end
  opt HEALTH_SHADOW=true
    Alarm->>Tel: AssetHealthPredicted modelVersion=health-v2
    Note over Billing: no second saga
  end
  Tel->>Dash: v1 → asset_health_latest, v2 → asset_health_shadow
  Note over Dash: GET /dashboard/fleet still v1
  Dash->>Dash: POST /dashboard/cutover?to=health-v2
  Dash->>Dash: copy shadow rows, set health_model meta
```

Handler decision (Alarm):

```mermaid
flowchart TD
  R[SensorReadingReported or Corrected] --> Live["score with HEALTH_MODEL"]
  Live --> Pred[emit AssetHealthPredicted live]
  Pred --> Alert{"live score crosses raise/resolve?"}
  Alert -->|yes| HA[HealthAlertRaised or Resolved]
  Alert -->|no| Skip[no alert]
  Live --> Shadow{"HEALTH_SHADOW?"}
  Shadow -->|yes| Pred2["emit AssetHealthPredicted other version"]
  Shadow -->|no| Done[stop]
  Pred2 --> Done
```

Mis-demo / failure branches this slice implements:

- Mixed log: old `AssetHealthPredicted` v1 upcasts; `EventId` / `TraceId` preserved
- Shadow v2 does not double-create Billing sagas
- Cutover is explicit; live fleet scores stay v1 until `POST /dashboard/cutover`
- Rebuild truncate-replay (Step 5) still works; duck hour buckets unchanged

## How to test

```bash
dotnet test --filter "FullyQualifiedName~OrePartUpcaster|FullyQualifiedName~HealthCorrection|FullyQualifiedName~LateData"
dotnet test
```

Aspire: Alarm `HEALTH_SHADOW=true`. Compare Dashboard fleet vs shadow after a degraded `TRK-001` run, then `POST /dashboard/cutover?to=health-v2`.
