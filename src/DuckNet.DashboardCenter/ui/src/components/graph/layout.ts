import { Graph, layout } from "@dagrejs/dagre";
import type { Edge, Node } from "@vue-flow/core";

export const nodeSize: Record<string, { width: number; height: number }> = {
  step: { width: 220, height: 56 },
  decision: { width: 160, height: 148 },
  store: { width: 210, height: 56 },
  drop: { width: 88, height: 40 },
  object: { width: 210, height: 68 },
  center: { width: 156, height: 156 },
  port: { width: 210, height: 80 },
};

export function sizeFor(type: string | undefined): { width: number; height: number } {
  return nodeSize[type ?? "step"] ?? nodeSize.step;
}

export function layoutGraph(nodes: Node[], edges: Edge[], rankdir: "TB" | "LR"): Node[] {
  const g = new Graph();
  g.setDefaultEdgeLabel(() => ({}));
  g.setGraph({
    rankdir,
    nodesep: 56,
    edgesep: 28,
    ranksep: rankdir === "TB" ? 80 : 88,
    marginx: 16,
    marginy: 16,
  });

  for (const node of nodes) {
    const size = sizeFor(node.type);
    g.setNode(node.id, { width: size.width, height: size.height });
  }
  for (const edge of edges) {
    g.setEdge(edge.source, edge.target);
  }

  layout(g);

  return nodes.map((node) => {
    const placed = g.node(node.id);
    const size = sizeFor(node.type);
    return {
      ...node,
      position: {
        x: (placed?.x ?? 0) - size.width / 2,
        y: (placed?.y ?? 0) - size.height / 2,
      },
      width: size.width,
      height: size.height,
    };
  });
}

export function boundsOf(nodes: Node[]): { width: number; height: number } {
  let maxX = 0;
  let maxY = 0;
  for (const node of nodes) {
    const size = sizeFor(node.type);
    maxX = Math.max(maxX, node.position.x + size.width);
    maxY = Math.max(maxY, node.position.y + size.height);
  }
  return { width: maxX, height: maxY };
}
