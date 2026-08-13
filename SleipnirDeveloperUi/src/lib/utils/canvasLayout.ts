// Pure layout + geometry helpers for the dependency query-designer canvas.
// No Svelte, no Sleipnir types — depends only on the `Point` shape from
// canvasViewport. The DepStep-aware integration (autoLayoutSteps/ensurePositions/
// deriveEdges) lives in canvasGraph.ts.
//
// Node metrics are fixed (NODE_WIDTH, port row height) so committed-edge anchors
// can be computed predictively from a node's x/y without measuring the DOM —
// the DOM is only read for the in-progress drag (see DepCanvas.svelte).

import type { Point } from './canvasViewport';

export interface LayoutEdge {
  /** Provider step id. */
  from: string;
  /** Consumer step id. */
  to: string;
}

// --- Node metrics (canvas-space px) -----------------------------------------

export const NODE_WIDTH = 260;
export const HEADER_HEIGHT = 34;
export const PORT_ROW_HEIGHT = 22;
/** Vertical room reserved for the expandable return-schema tree in a node. */
export const SCHEMA_RESERVE = 28;
export const PADDING = 28;
export const COL_GAP = 96;
export const ROW_GAP = 28;
/** Default node height estimate used for auto-layout row spacing. */
export const NODE_HEIGHT_DEFAULT = 150;

/** Estimated height of a node given how many input + output (expose) ports it has. */
export function nodeHeight(inputPorts: number, outputPorts: number): number {
  const rows = Math.max(inputPorts, outputPorts, 1);
  return HEADER_HEIGHT + rows * PORT_ROW_HEIGHT + SCHEMA_RESERVE;
}

export type PortSide = 'input' | 'output';

/** Anchor point (canvas space) of a port on a node, by side + port index.
 *  Input ports sit on the left edge, output ports on the right edge. */
export function portAnchor(nodePos: Point, side: PortSide, portIndex: number): Point {
  const x = side === 'input' ? nodePos.x : nodePos.x + NODE_WIDTH;
  const y = nodePos.y + HEADER_HEIGHT + portIndex * PORT_ROW_HEIGHT + PORT_ROW_HEIGHT / 2;
  return { x, y };
}

/** Cubic bezier path string between two canvas-space points (for SVG `d`). */
export function bezierPath(a: Point, b: Point): string {
  const dx = Math.abs(b.x - a.x);
  const cx = Math.max(40, dx * 0.5);
  return `M ${a.x} ${a.y} C ${a.x + cx} ${a.y}, ${b.x - cx} ${b.y}, ${b.x} ${b.y}`;
}

// --- Layered auto-layout (topological by @alias edges) ----------------------

/** Assign a column level to each step: level[consumer] = max(level[provider] + 1),
 *  roots = 0. Cycle-guard via a bounded fixpoint iteration. Returns a map
 *  stepId → level. Pure; deterministic. */
export function assignLevels(stepIds: string[], edges: LayoutEdge[]): Map<string, number> {
  const level = new Map<string, number>();
  for (const id of stepIds) level.set(id, 0);
  // Fixpoint: relax along edges. Bound iterations to |nodes|+1 to break cycles.
  for (let iter = 0; iter < stepIds.length + 1; iter++) {
    let changed = false;
    for (const e of edges) {
      const fl = level.get(e.from) ?? 0;
      const tl = level.get(e.to) ?? 0;
      if (fl + 1 > tl) {
        level.set(e.to, fl + 1);
        changed = true;
      }
    }
    if (!changed) break;
  }
  return level;
}

/** Layered topological layout: column = level, rows stacked within each column.
 *  Returns a map stepId → canvas-space position. Pure. */
export function autoLayout(stepIds: string[], edges: LayoutEdge[]): Map<string, Point> {
  const level = assignLevels(stepIds, edges);
  const columns = new Map<number, string[]>();
  // Preserve input order within a column for stable layout.
  for (const id of stepIds) {
    const l = level.get(id) ?? 0;
    if (!columns.has(l)) columns.set(l, []);
    columns.get(l)!.push(id);
  }
  const pos = new Map<string, Point>();
  for (const [l, ids] of columns) {
    ids.forEach((id, i) => {
      pos.set(id, {
        x: PADDING + l * (NODE_WIDTH + COL_GAP),
        y: PADDING + i * (NODE_HEIGHT_DEFAULT + ROW_GAP),
      });
    });
  }
  return pos;
}