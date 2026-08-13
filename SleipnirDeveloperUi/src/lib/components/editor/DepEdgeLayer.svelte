<script lang="ts">
  // Pure SVG edge layer for the dependency canvas. Renders one cubic bezier per
  // committed edge (colored by static-binding severity) + an `@alias` label badge,
  // plus the in-progress drag bezier. The whole layer is `pointer-events:none`
  // except committed edge paths/labels (click-to-select), and even those are
  // disabled while a drag is in progress so they never intercept the hit-test.
  //
  // Pan/zoom are applied via a single `transform` on the wrapping `<g>` (passed in
  // from DepCanvas, which keeps it identical to the node-layer transform so edges
  // and nodes stay pixel-locked). No per-edge recomputation on pan.

  import type { Point } from '../../utils/canvasViewport';
  import type { EdgeSeverity } from '../../utils/canvasGraph';

  export interface RenderEdge {
    id: string;
    alias: string;
    severity: EdgeSeverity;
    fromAnchor: Point;
    toAnchor: Point;
  }

  let {
    transform,
    edges,
    pending = null,
    selectedEdgeId = null,
    dragging = false,
    onselectedge,
  }: {
    transform: string;
    edges: RenderEdge[];
    /** Pending drag bezier endpoints (canvas coords), or null when no drag. */
    pending: { from: Point; to: Point } | null;
    selectedEdgeId?: string | null;
    dragging?: boolean;
    onselectedge?: (id: string) => void;
  } = $props();

  function strokeOf(sev: EdgeSeverity, selected: boolean): string {
    if (selected) return 'var(--accent)';
    switch (sev) {
      case 'error': return 'var(--error)';
      case 'warn': return 'var(--warning)';
      case 'info': return 'var(--text-muted)';
      default: return 'var(--accent-secondary)';
    }
  }

  function midpoint(a: Point, b: Point): Point {
    return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
  }

  // Cubic bezier control points (mirrors canvasLayout.bezierPath, kept local so
  // the layer is self-contained for the label anchor).
  function bez(a: Point, b: Point): string {
    const dx = Math.abs(b.x - a.x);
    const cx = Math.max(40, dx * 0.5);
    return `M ${a.x} ${a.y} C ${a.x + cx} ${a.y}, ${b.x - cx} ${b.y}, ${b.x} ${b.y}`;
  }

  function selectEdge(id: string, ev: Event): void {
    if (dragging) return;
    ev.stopPropagation();
    onselectedge?.(id);
  }

  function onEdgeKeydown(id: string, ev: KeyboardEvent): void {
    if (dragging) return;
    if (ev.key === 'Enter' || ev.key === ' ') {
      ev.preventDefault();
      ev.stopPropagation();
      onselectedge?.(id);
    }
  }
</script>

<svg class="edge-layer" aria-hidden="true">
  <g {transform}>
    {#each edges as e (e.id)}
      {@const selected = e.id === selectedEdgeId}
      {@const mid = midpoint(e.fromAnchor, e.toAnchor)}
      <path
        class="edge"
        class:selectable={!dragging}
        class:selected
        d={bez(e.fromAnchor, e.toAnchor)}
        stroke={strokeOf(e.severity, selected)}
        role="button"
        tabindex={-1}
        aria-label={`Kante @${e.alias} auswählen`}
        onclick={(ev) => selectEdge(e.id, ev)}
        onkeydown={(ev) => onEdgeKeydown(e.id, ev)}
      />
      <g
        class="edge-label"
        class:selectable={!dragging}
        class:selected
        transform={`translate(${mid.x}, ${mid.y})`}
        role="button"
        tabindex={-1}
        aria-label={`Kante @${e.alias} auswählen`}
        onclick={(ev) => selectEdge(e.id, ev)}
        onkeydown={(ev) => onEdgeKeydown(e.id, ev)}
      >
        <rect x="-26" y="-9" width="52" height="18" rx="9" />
        <text x="0" y="4" text-anchor="middle">@{e.alias}</text>
      </g>
    {/each}

    {#if pending}
      <path class="pending" d={bez(pending.from, pending.to)} />
      <circle cx={pending.from.x} cy={pending.from.y} r="4" class="pending-dot" />
    {/if}
  </g>
</svg>

<style>
  .edge-layer {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    pointer-events: none;
    overflow: visible;
    z-index: 1;
  }
  .edge {
    fill: none;
    stroke-width: 2;
    pointer-events: none;
  }
  .edge.selectable {
    pointer-events: stroke;
    cursor: pointer;
  }
  .edge.selected {
    stroke-width: 3;
  }
  .edge-label {
    pointer-events: none;
  }
  .edge-label.selectable {
    pointer-events: all;
    cursor: pointer;
  }
  .edge-label rect {
    fill: var(--bg-elevated);
    stroke: var(--border);
    stroke-width: 1;
  }
  .edge-label.selected rect {
    stroke: var(--accent);
  }
  .edge-label text {
    fill: var(--text);
    font-family: var(--font-mono);
    font-size: 11px;
  }
  .pending {
    fill: none;
    stroke: var(--accent);
    stroke-width: 2;
    stroke-dasharray: 5 4;
    pointer-events: none;
  }
  .pending-dot {
    fill: var(--accent);
    pointer-events: none;
  }
</style>