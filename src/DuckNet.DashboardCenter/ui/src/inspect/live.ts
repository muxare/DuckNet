import type { CenterId } from "../system-map";
import type { LiveSnapshot } from "../useLive";
import type { InspectContext, LiveFact } from "./types";

export const livePanelTermIds = [
  "last-offset",
  "catch-up",
  "log-count",
  "alarm-count",
  "dlq-count",
  "threshold",
  "window",
  "shard-lag",
  "saga-state",
  "saga-timeout",
  "reserved",
  "released",
  "expired",
  "total-squeaks",
  "dlq",
  "rebuild",
  "alarms",
] as const;

export type LiveStrip = {
  facts: LiveFact[];
  hint?: string;
};

function fmt(value: number | undefined): string {
  if (value === undefined) {
    return "—";
  }
  return value.toLocaleString();
}

function scopedOffset(centerId: CenterId | undefined, snap: LiveSnapshot): number | undefined {
  if (centerId === "telemetry") {
    return snap.telemetry?.lastOffset;
  }
  if (centerId === "alarm") {
    return snap.alarm?.lastOffset;
  }
  if (centerId === "dashboard") {
    return snap.dashboard?.lastOffset;
  }
  if (centerId === "billing") {
    return snap.billing?.lastOffset;
  }
  return undefined;
}

function scopedCatchUp(centerId: CenterId | undefined, snap: LiveSnapshot): number | undefined {
  if (centerId === "alarm") {
    return snap.alarmCatchUp;
  }
  if (centerId === "dashboard") {
    return snap.dashboardCatchUp;
  }
  if (centerId === "billing") {
    return snap.billingCatchUp;
  }
  return undefined;
}

function scopedShards(centerId: CenterId | undefined, snap: LiveSnapshot) {
  if (centerId === "alarm") {
    return snap.alarm?.shards ?? [];
  }
  if (centerId === "dashboard") {
    return snap.dashboardMetrics?.shards ?? snap.dashboard?.shards ?? [];
  }
  if (centerId === "billing") {
    return snap.billing?.shards ?? [];
  }
  return [];
}

function scopedDlq(centerId: CenterId | undefined, snap: LiveSnapshot): number | undefined {
  if (centerId === "alarm") {
    return snap.alarm?.dlqCount;
  }
  if (centerId === "dashboard") {
    return snap.dashboard?.dlqCount;
  }
  if (centerId === "billing") {
    return snap.billing?.dlqCount;
  }
  return undefined;
}

const noScopeHint: Record<string, string> = {
  "last-offset": "Hover a Center’s offset to see the live cursor.",
  "catch-up": "Hover a consumer’s behind-log figure to see catch-up.",
  "log-count": "Hover Telemetry’s log count to see the live head.",
  "alarm-count": "Hover AlarmCenter’s alarm count to see the live total.",
  "dlq-count": "Hover a Center’s DLQ count to see the live queue.",
  threshold: "Hover AlarmCenter’s threshold to see the live knob.",
  window: "Hover AlarmCenter’s window to see the live seconds.",
  "shard-lag": "Hover a shard chip to see live queue and lag.",
  "saga-state": "Hover a Billing saga row to see live counts.",
  "saga-timeout": "Hover Billing’s timeout to see the live seconds.",
  reserved: "Hover Billing’s reserved count to see the live total.",
  released: "Hover Billing’s released count to see the live total.",
  expired: "Hover Billing’s expired count to see the live total.",
  "total-squeaks": "Hover Dashboard’s squeak total to see the live count.",
  dlq: "Hover a Center’s DLQ to see the live depth.",
  rebuild: "Hover Dashboard rebuild to inspect the projector.",
  alarms: "Hover AlarmCenter’s alarm list to see live rows.",
  center: "Hover a Center on the map to see live stats.",
};

function needsScope(binder: string, ctx: InspectContext): boolean {
  if (binder === "center") {
    return !ctx.centerId;
  }
  return !ctx.centerId;
}

export function liveFacts(binder: string, ctx: InspectContext, snap: LiveSnapshot): LiveStrip {
  if (needsScope(binder, ctx)) {
    return { facts: [], hint: noScopeHint[binder] ?? "Hover the matching live number to annotate this card." };
  }

  const id = ctx.centerId;

  if (binder === "center") {
    if (id === "telemetry") {
      return {
        facts: [
          { label: "log", value: fmt(snap.telemetry?.logCount) },
          { label: "offset", value: fmt(snap.telemetry?.lastOffset) },
        ],
      };
    }
    if (id === "alarm") {
      return {
        facts: [
          { label: "offset", value: fmt(snap.alarm?.lastOffset) },
          { label: "behind log", value: fmt(snap.alarmCatchUp) },
          { label: "alarms", value: fmt(snap.alarm?.alarmCount) },
          { label: "dlq", value: fmt(snap.alarm?.dlqCount) },
        ],
      };
    }
    if (id === "dashboard") {
      return {
        facts: [
          { label: "offset", value: fmt(snap.dashboard?.lastOffset) },
          { label: "behind log", value: fmt(snap.dashboardCatchUp) },
          { label: "squeaks", value: fmt(snap.dashboard?.totalSqueaks) },
          { label: "dlq", value: fmt(snap.dashboard?.dlqCount) },
        ],
      };
    }
    if (id === "billing") {
      return {
        facts: [
          { label: "offset", value: fmt(snap.billing?.lastOffset) },
          { label: "behind log", value: fmt(snap.billingCatchUp) },
          { label: "reserved", value: fmt(snap.billing?.reserved) },
          { label: "released", value: fmt(snap.billing?.released) },
          { label: "expired", value: fmt(snap.billing?.expired) },
        ],
      };
    }
    return { facts: [], hint: "IEventBus has no Center stats. Aspire hosts RabbitMQ; tests use InMemoryEventBus." };
  }

  if (binder === "last-offset") {
    const facts: LiveFact[] = [{ label: "now", value: fmt(scopedOffset(id, snap)) }];
    const behind = scopedCatchUp(id, snap);
    if (behind !== undefined) {
      facts.push({ label: "behind log", value: fmt(behind) });
    }
    return { facts };
  }

  if (binder === "catch-up") {
    return { facts: [{ label: "behind log", value: fmt(scopedCatchUp(id, snap)) }] };
  }

  if (binder === "log-count") {
    return { facts: [{ label: "log rows", value: fmt(snap.telemetry?.logCount) }] };
  }

  if (binder === "alarm-count") {
    return { facts: [{ label: "alarms", value: fmt(snap.alarm?.alarmCount) }] };
  }

  if (binder === "dlq-count" || binder === "dlq") {
    return { facts: [{ label: "dlq", value: fmt(scopedDlq(id, snap)) }] };
  }

  if (binder === "threshold") {
    return { facts: [{ label: "threshold", value: fmt(snap.alarm?.threshold) }] };
  }

  if (binder === "window") {
    return { facts: [{ label: "window", value: `${fmt(snap.alarm?.windowSeconds)}s` }] };
  }

  if (binder === "shard-lag") {
    const shards = scopedShards(id, snap);
    if (shards.length === 0) {
      return { facts: [], hint: "No shard snapshot yet." };
    }
    return {
      facts: shards.map((shard) => ({
        label: `shard ${shard.id}`,
        value: `q ${shard.queued} · lag ${shard.lag}`,
      })),
    };
  }

  if (binder === "saga-state") {
    return {
      facts: [
        { label: "reserved", value: fmt(snap.billing?.reserved) },
        { label: "released", value: fmt(snap.billing?.released) },
        { label: "expired", value: fmt(snap.billing?.expired) },
      ],
    };
  }

  if (binder === "saga-timeout") {
    return { facts: [{ label: "timeout", value: `${fmt(snap.billing?.sagaTimeoutSeconds)}s` }] };
  }

  if (binder === "reserved") {
    return { facts: [{ label: "reserved", value: fmt(snap.billing?.reserved) }] };
  }

  if (binder === "released") {
    return { facts: [{ label: "released", value: fmt(snap.billing?.released) }] };
  }

  if (binder === "expired") {
    return { facts: [{ label: "expired", value: fmt(snap.billing?.expired) }] };
  }

  if (binder === "total-squeaks") {
    return { facts: [{ label: "squeaks", value: fmt(snap.dashboard?.totalSqueaks) }] };
  }

  if (binder === "alarms") {
    return { facts: [{ label: "active", value: String(snap.alarms.length) }] };
  }

  if (binder === "rebuild") {
    return {
      facts: [
        { label: "offset", value: fmt(snap.dashboard?.lastOffset) },
        { label: "squeaks", value: fmt(snap.dashboard?.totalSqueaks) },
      ],
    };
  }

  return { facts: [] };
}
