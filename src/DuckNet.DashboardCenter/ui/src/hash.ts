import type { CenterId } from "./system-map";

export type AppView = "developer" | "read-model";

export type Zoom =
  | { kind: "overview" }
  | { kind: "all" }
  | { kind: "center"; id: CenterId };

const centerIds: CenterId[] = ["telemetry", "alarm", "dashboard", "billing", "bus"];

export function parseHash(hash: string): { view: AppView; zoom: Zoom } {
  const raw = hash.replace(/^#/, "").replace(/^\//, "");
  if (raw === "read-model" || raw.startsWith("read-model/")) {
    return { view: "read-model", zoom: { kind: "overview" } };
  }
  const path = raw.startsWith("developer") ? raw.slice("developer".length).replace(/^\//, "") : raw;
  if (path === "all") {
    return { view: "developer", zoom: { kind: "all" } };
  }
  if (centerIds.includes(path as CenterId)) {
    return { view: "developer", zoom: { kind: "center", id: path as CenterId } };
  }
  return { view: "developer", zoom: { kind: "overview" } };
}

export function hashFor(view: AppView, zoom: Zoom = { kind: "overview" }): string {
  if (view === "read-model") {
    return "#read-model";
  }
  if (zoom.kind === "all") {
    return "#developer/all";
  }
  if (zoom.kind === "center") {
    return `#developer/${zoom.id}`;
  }
  return "#developer";
}

export function isCenterId(value: string): value is CenterId {
  return centerIds.includes(value as CenterId);
}
