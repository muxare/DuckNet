import { MarkerType, type Edge, type Node } from "@vue-flow/core";
import {
  centerById,
  centers,
  overviewGraph,
  type CenterId,
  type GraphKind,
  type MapEdge,
} from "../../system-map";
import { boundsOf, layoutGraph, sizeFor } from "./layout";

export const accents: Record<CenterId, string> = {
  telemetry: "#e6b422",
  alarm: "#c2410c",
  dashboard: "#0f766e",
  billing: "#1d4ed8",
  bus: "#64748b",
};

const kindColor: Record<GraphKind, string> = {
  event: "#c2410c",
  http: "#64748b",
  internal: "#1b2430",
};

export function flowEdges(edges: MapEdge[], idPrefix = ""): Edge[] {
  return edges.map((edge, index) => ({
    id: `${idPrefix}${edge.source}->${edge.target}-${index}`,
    source: idPrefix ? `${idPrefix}${edge.source}` : edge.source,
    target: idPrefix ? `${idPrefix}${edge.target}` : edge.target,
    sourceHandle: edge.sourceHandle,
    targetHandle: edge.targetHandle,
    label: edge.label,
    type: "smoothstep",
    markerEnd: MarkerType.ArrowClosed,
    data: { termId: edge.term },
    style: {
      stroke: kindColor[edge.kind ?? "internal"],
      strokeWidth: 1.7,
    },
    labelStyle: { fill: "#1b2430", fontSize: 11, fontWeight: 600 },
    labelBgStyle: { fill: "#f4f1ea" },
    labelBgPadding: [4, 2] as [number, number],
    labelBgBorderRadius: 4,
  }));
}

export function processNodes(centerId: CenterId, prefix = ""): Node[] {
  const graph = centerById[centerId].process;
  const nodes: Node[] = graph.nodes.map((node) => ({
    id: `${prefix}${node.id}`,
    type: node.kind,
    position: { x: 0, y: 0 },
    draggable: false,
    connectable: false,
    data: { label: node.label, note: node.note, centerId, termId: node.term },
    ...sizeFor(node.kind),
  }));
  return layoutGraph(nodes, flowEdges(graph.edges, prefix), "TB");
}

export function objectNodes(centerId: CenterId, prefix = ""): Node[] {
  const graph = centerById[centerId].objects;
  const nodes: Node[] = graph.nodes.map((node) => ({
    id: `${prefix}${node.id}`,
    type: "object",
    position: { x: 0, y: 0 },
    draggable: false,
    connectable: false,
    data: { label: node.label, note: node.role, centerId, termId: node.term },
    ...sizeFor("object"),
  }));
  return layoutGraph(nodes, flowEdges(graph.edges, prefix), "LR");
}

export function centerProcessFlow(centerId: CenterId): { nodes: Node[]; edges: Edge[] } {
  const graph = centerById[centerId].process;
  const nodes = processNodes(centerId);
  return { nodes, edges: flowEdges(graph.edges) };
}

export function centerObjectFlow(centerId: CenterId): { nodes: Node[]; edges: Edge[] } {
  const graph = centerById[centerId].objects;
  const nodes = objectNodes(centerId);
  return { nodes, edges: flowEdges(graph.edges) };
}

const GROUP_GAP = 64;
const GROUP_PAD = 24;
const GROUP_HEADER = 56;

export function allDetailFlow(): { nodes: Node[]; edges: Edge[] } {
  const nodes: Node[] = [];
  const edges: Edge[] = [];
  let x = 0;

  for (const center of centers) {
    const prefix = `${center.id}__`;
    const children = processNodes(center.id, prefix);
    const box = boundsOf(children);
    const width = Math.max(box.width + GROUP_PAD * 2, 240);
    const height = box.height + GROUP_HEADER + GROUP_PAD;
    const parentId = `g-${center.id}`;

    nodes.push({
      id: parentId,
      type: "group",
      position: { x, y: 0 },
      draggable: false,
      connectable: false,
      selectable: true,
      style: { width, height },
      width,
      height,
      data: {
        centerId: center.id,
        title: center.title,
        kicker: center.role,
        accent: accents[center.id],
        termId: center.term,
      },
    });

    for (const child of children) {
      nodes.push({
        ...child,
        parentNode: parentId,
        extent: "parent",
        position: {
          x: child.position.x + GROUP_PAD,
          y: child.position.y + GROUP_HEADER,
        },
        data: { ...child.data, centerId: center.id },
      });
    }

    edges.push(...flowEdges(center.process.edges, prefix));
    x += width + GROUP_GAP;
  }

  return { nodes, edges };
}

export function overviewNodes(): Node[] {
  return overviewGraph.nodes.map((node) => {
    const meta = centerById[node.id];
    return {
      id: node.id,
      type: node.type,
      position: node.position,
      draggable: false,
      connectable: false,
      data: {
        centerId: node.id,
        title: meta.title,
        kicker: meta.role,
        accent: accents[node.id],
        termId: meta.term,
        lines: [] as string[],
      },
      ...sizeFor(node.type),
    };
  });
}

export function overviewEdges(): Edge[] {
  return flowEdges(overviewGraph.edges);
}
