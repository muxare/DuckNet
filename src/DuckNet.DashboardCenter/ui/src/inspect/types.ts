import type { CenterId } from "../system-map";

export type TermKind = "center" | "component" | "event" | "table" | "metric" | "rule" | "concept";

export type Term = {
  id: string;
  title: string;
  kind: TermKind;
  summary: string;
  body: string;
  live?: string;
  code?: { path: string; why: string }[];
};

export type InspectContext = {
  centerId?: CenterId;
};

export type PinFrame = {
  termId: string;
  x: number;
  y: number;
  context: InspectContext;
};

export type InspectStackState = {
  preview: PinFrame | null;
  pins: PinFrame[];
};

export type LiveFact = {
  label: string;
  value: string;
};
