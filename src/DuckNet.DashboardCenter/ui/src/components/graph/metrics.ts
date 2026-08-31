import type { CenterId } from "../../system-map";
import type { LiveSnapshot } from "../../useLive";

function format(value: number | undefined): string {
  if (value === undefined) {
    return "—";
  }
  return value.toLocaleString();
}

function errorText(err: unknown): string | undefined {
  return err instanceof Error ? err.message : err ? String(err) : undefined;
}

export type NodeMetrics = {
  lines: string[];
  error?: string;
};

export function metricsFor(id: CenterId, snap: LiveSnapshot): NodeMetrics {
  if (id === "telemetry") {
    const err = errorText(snap.telemetryError);
    if (err) {
      return { lines: [], error: err };
    }
    if (!snap.telemetryUrl) {
      return { lines: ["no catalog URL"] };
    }
    if (!snap.telemetry) {
      return { lines: ["loading…"] };
    }
    return { lines: [`log ${format(snap.telemetry.logCount)}`, `off ${format(snap.telemetry.lastOffset)}`] };
  }

  if (id === "alarm") {
    const err = errorText(snap.alarmError);
    if (err) {
      return { lines: [], error: err };
    }
    if (!snap.alarmUrl) {
      return { lines: ["no catalog URL"] };
    }
    if (!snap.alarm) {
      return { lines: ["loading…"] };
    }
    return {
      lines: [
        `off ${format(snap.alarm.lastOffset)} · lag ${format(snap.alarmCatchUp)}`,
        `alarms ${format(snap.alarm.alarmCount)} · dlq ${format(snap.alarm.dlqCount)}`,
      ],
    };
  }

  if (id === "dashboard") {
    const err = errorText(snap.dashboardError);
    if (err) {
      return { lines: [], error: err };
    }
    if (!snap.dashboard) {
      return { lines: ["loading…"] };
    }
    return {
      lines: [
        `off ${format(snap.dashboard.lastOffset)} · lag ${format(snap.dashboardCatchUp)}`,
        `squeaks ${format(snap.dashboard.totalSqueaks)} · dlq ${format(snap.dashboard.dlqCount)}`,
      ],
    };
  }

  if (id === "billing") {
    const err = errorText(snap.billingError);
    if (err) {
      return { lines: [], error: err };
    }
    if (!snap.billingUrl) {
      return { lines: ["no catalog URL"] };
    }
    if (!snap.billing) {
      return { lines: ["loading…"] };
    }
    return {
      lines: [
        `off ${format(snap.billing.lastOffset)} · lag ${format(snap.billingCatchUp)}`,
        `${format(snap.billing.reserved)}R / ${format(snap.billing.released)}L / ${format(snap.billing.expired)}X`,
      ],
    };
  }

  return { lines: ["Squeaked · Alarm* · Fee*"] };
}
