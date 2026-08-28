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
};

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
  return (await response.json()) as T;
}

export function fetchSummary(): Promise<DashboardSummary> {
  return fetch("/dashboard/summary").then((r) => readJson<DashboardSummary>(r));
}

export function fetchStats(): Promise<DashboardStats> {
  return fetch("/stats").then((r) => readJson<DashboardStats>(r));
}

export async function rebuildDashboard(): Promise<void> {
  const response = await fetch("/dashboard/rebuild", { method: "POST" });
  if (!response.ok && response.status !== 202) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
}
