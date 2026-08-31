export type SqueakHourRow = {
  duckId: string;
  hourUtc: string;
  count: number;
  volumeDb: number;
};

export type DashboardSummary = {
  rows: SqueakHourRow[];
  totalSqueaks: number;
  rowCount: number;
  totalVolumeDb: number;
};

export type DashboardStats = {
  totalSqueaks: number;
  totalVolumeDb: number;
  rowCount: number;
  lastOffset: number;
  database: string;
  dlqCount?: number;
  shardCount?: number;
  shards?: ShardSnapshot[];
  keys?: KeyLagSnapshot[];
};

export type ShardSnapshot = {
  id: number;
  queued: number;
  lag: number;
  lastOffset: number;
  maxOffset: number;
  backpressure: number;
  processed: number;
};

export type KeyLagSnapshot = {
  partitionKey: string;
  shard: number;
  lastLagMs: number;
  maxLagMs: number;
  processed: number;
};

export type ShardMetrics = {
  shards: ShardSnapshot[];
  keys: KeyLagSnapshot[];
};

export type UiCatalog = {
  telemetry: string;
  alarm: string;
  billing: string;
  dashboard: string;
};

export type TelemetryStats = {
  logCount: number;
  lastOffset: number;
  database: string;
};

export type AlarmStats = {
  alarmCount: number;
  lastOffset: number;
  database: string;
  threshold: number;
  windowSeconds: number;
  dlqCount: number;
  raisedCount: number;
  resolvedCount: number;
  shardCount?: number;
  shards?: ShardSnapshot[];
  keys?: KeyLagSnapshot[];
};

export type BillingStats = {
  sagaCount: number;
  reserved: number;
  released: number;
  expired: number;
  lastOffset: number;
  database: string;
  feeAmountCents: number;
  sagaTimeoutSeconds: number;
  timeoutExpiredCount: number;
  reservedCount: number;
  releasedCount: number;
  dlqCount: number;
  shards?: ShardSnapshot[];
  keys?: KeyLagSnapshot[];
};

export type AlarmRow = {
  duckId: string;
  rate: number;
  windowStart: string;
  raisedAt: string;
  eventId: string;
};

export type SagaRow = {
  alarmId: string;
  duckId: string;
  state: string;
  amountCents: number;
  reservedAt: string;
  expiresAt: string;
};

export type DlqRow = {
  id: number;
  consumerGroup: string;
  eventId: string;
  payloadJson: string;
  error: string;
  failedAt: string;
  attempts: number;
};

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
  return (await response.json()) as T;
}

export function centerUrl(base: string, path: string): string {
  const suffix = path.startsWith("/") ? path : `/${path}`;
  if (!base) {
    return suffix;
  }
  return `${base.replace(/\/$/, "")}${suffix}`;
}

export function fetchCatalog(): Promise<UiCatalog> {
  return fetch("/ui/catalog").then((r) => readJson<UiCatalog>(r));
}

export function fetchCenterJson<T>(base: string, path: string): Promise<T> {
  return fetch(centerUrl(base, path)).then((r) => readJson<T>(r));
}

export async function postCenter(base: string, path: string, body?: unknown): Promise<void> {
  const response = await fetch(centerUrl(base, path), {
    method: "POST",
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok && response.status !== 202) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
}

export function fetchSummary(): Promise<DashboardSummary> {
  return fetch("/dashboard/summary").then((r) => readJson<DashboardSummary>(r));
}

export function fetchStats(): Promise<DashboardStats> {
  return fetch("/stats").then((r) => readJson<DashboardStats>(r));
}

export function fetchMetrics(): Promise<ShardMetrics> {
  return fetch("/metrics").then((r) => readJson<ShardMetrics>(r));
}

export async function rebuildDashboard(): Promise<void> {
  const response = await fetch("/dashboard/rebuild", { method: "POST" });
  if (!response.ok && response.status !== 202) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
}

export function fileName(path: string | undefined): string {
  if (!path) {
    return "—";
  }
  const parts = path.replace(/\\/g, "/").split("/");
  return parts[parts.length - 1] || path;
}

export function catchUp(logHead: number | undefined, lastOffset: number | undefined): number | undefined {
  if (logHead === undefined || lastOffset === undefined) {
    return undefined;
  }
  return logHead - lastOffset;
}
