<script lang="ts">
  // Dependency query-designer canvas. Renders DepStep[] as draggable nodes on a
  // pannable (wheel-zoomable) SVG+HTML canvas, draws @alias edges as SVG beziers
  // colored by the static binding checker, and implements the drag-to-connect
  // gesture: drag from a provider return-schema field onto a consumer parameter
  // port → commits an Expose + @alias binding (canvasGraph.commitEdge), validated
  // live by dependencyCheck.
  //
  // One unified `drag` state covers pan / node-move / edge-connect (pointer
  // capture on the container, so the gesture survives the cursor leaving the
  // canvas or crossing into the inspector). Committed-edge anchors are $derived
  // from step positions (never $state) so they never go stale on pan/zoom.

  import { discoveryState } from '../../state/discovery.svelte.ts';
  import { tabState, type Tab } from '../../state/tabs.svelte.ts';
  import type { Point } from '../../utils/canvasViewport';
  import { pointerToCanvas, rectCenterToCanvas, clampZoom, zoomAboutCursor } from '../../utils/canvasViewport';
  import { portAnchor, NODE_WIDTH } from '../../utils/canvasLayout';
  import {
    ensurePositions, deriveEdges, commitEdge, removeEdge, generateAlias, createStep, nextDefaultStepId,
  } from '../../utils/canvasGraph';
  import { methodMetaFor, type AliasProvider } from '../../utils/dependencyCheck';
  import DepNode from './DepNode.svelte';
  import DepEdgeLayer, { type RenderEdge } from './DepEdgeLayer.svelte';

  let { tab, selectedNodeId, onselectnode, resetViewSignal = 0 }: {
    tab: Tab;
    selectedNodeId: string | null;
    onselectnode: (id: string | null) => void;
    /** Increment to reset pan/zoom to the default view (toolbar „Neu anordnen"/„Zoom-Reset"). */
    resetViewSignal?: number;
  } = $props();

  let containerEl = $state<HTMLDivElement | null>(null);
  let pan = $state<Point>({ x: 28, y: 28 });
  let zoom = $state(1);
  let selectedEdgeId = $state<string | null>(null);

  type Rect = { left: number; top: number };
  type Drag =
    | { kind: 'pan'; startClient: Point; startPan: Point; pointerId: number; rect: Rect }
    | { kind: 'node'; stepId: string; startClient: Point; startPos: Point; moved: boolean; pointerId: number; rect: Rect }
    | { kind: 'edge'; sourceStepId: string; sourceJsonPath: string; sourceAnchor: Point; pointer: Point; targetStepId: string | null; targetParamName: string | null; pointerId: number; rect: Rect };
  let drag = $state<Drag | null>(null);

  let steps = $derived(tab.steps ?? []);
  let positions = $derived(ensurePositions(steps));
  let edges = $derived.by(() => deriveEdges(steps, discoveryState.data));

  let renderEdges = $derived.by<RenderEdge[]>(() =>
    edges.map((e) => {
      const fp = positions.get(e.fromStepId);
      const tp = positions.get(e.toStepId);
      return {
        id: e.id,
        alias: e.alias,
        severity: e.severity,
        fromAnchor: fp ? portAnchor(fp, 'output', e.fromPortIndex) : { x: 0, y: 0 },
        toAnchor: tp ? portAnchor(tp, 'input', e.toPortIndex) : { x: 0, y: 0 },
      };
    }),
  );

  let transform = $derived(`translate(${pan.x}px, ${pan.y}px) scale(${zoom})`);
  let pending = $derived(
    drag?.kind === 'edge'
      ? { from: drag.sourceAnchor, to: drag.pointer }
      : null,
  );

  // Toolbar-driven view reset. Skips the initial 0 so mount doesn't snap the view.
  $effect(() => {
    if (resetViewSignal) {
      pan = { x: 28, y: 28 };
      zoom = 1;
    }
  });

  /** alias → provider, built from steps *before* each index (Serial semantics). */
  function aliasProvidersFor(index: number): Record<string, AliasProvider> {
    const map: Record<string, AliasProvider> = {};
    for (const s of steps.slice(0, index)) {
      const mm = methodMetaFor(s, discoveryState.data);
      if (!mm) continue;
      for (const e of s.exposes) {
        if (e.alias) map[e.alias] = { methodMeta: mm, jsonPath: e.jsonPath };
      }
    }
    return map;
  }

  // --- pointer flow ---------------------------------------------------------

  function containerRect(): Rect {
    const r = containerEl?.getBoundingClientRect();
    return r ? { left: r.left, top: r.top } : { left: 0, top: 0 };
  }

  function onBackgroundDown(e: PointerEvent): void {
    if (e.button !== 0) return;
    // Clicking empty canvas deselects the node and any edge.
    onselectnode(null);
    selectedEdgeId = null;
    const rect = containerRect();
    drag = { kind: 'pan', startClient: { x: e.clientX, y: e.clientY }, startPan: { ...pan }, pointerId: e.pointerId, rect };
    containerEl?.setPointerCapture(e.pointerId);
    e.preventDefault();
  }

  function onStartNodeDrag(stepId: string, e: PointerEvent): void {
    const rect = containerRect();
    const startPos = positions.get(stepId) ?? { x: 28, y: 28 };
    drag = { kind: 'node', stepId, startClient: { x: e.clientX, y: e.clientY }, startPos: { ...startPos }, moved: false, pointerId: e.pointerId, rect };
    containerEl?.setPointerCapture(e.pointerId);
  }

  function onPortDragStart(jsonPath: string, el: HTMLElement, e: PointerEvent): void {
    const rect = containerRect();
    const sourceAnchor = rectCenterToCanvas(el.getBoundingClientRect(), rect, zoom);
    const stepId = (el.closest('[data-node-id]') as HTMLElement | null)?.dataset.nodeId ?? '';
    const pointer = pointerToCanvas(e.clientX, e.clientY, rect, zoom);
    drag = {
      kind: 'edge',
      sourceStepId: stepId,
      sourceJsonPath: jsonPath,
      sourceAnchor,
      pointer,
      targetStepId: null,
      targetParamName: null,
      pointerId: e.pointerId,
      rect,
    };
    containerEl?.setPointerCapture(e.pointerId);
  }

  function onPointerMove(e: PointerEvent): void {
    if (!drag) return;
    if (drag.kind === 'pan') {
      pan = { x: drag.startPan.x + (e.clientX - drag.startClient.x), y: drag.startPan.y + (e.clientY - drag.startClient.y) };
      return;
    }
    if (drag.kind === 'node') {
      const dx = (e.clientX - drag.startClient.x) / zoom;
      const dy = (e.clientY - drag.startClient.y) / zoom;
      const step = steps.find((s) => s.id === drag.stepId);
      if (step) {
        step.x = drag.startPos.x + dx;
        step.y = drag.startPos.y + dy;
        drag.moved = true;
      }
      return;
    }
    // edge
    drag.pointer = pointerToCanvas(e.clientX, e.clientY, drag.rect, zoom);
    const hit = hitTestInputPort(e.clientX, e.clientY);
    drag.targetStepId = hit?.stepId ?? null;
    drag.targetParamName = hit?.paramName ?? null;
  }

  function hitTestInputPort(clientX: number, clientY: number): { stepId: string; paramName: string } | null {
    const els = document.elementsFromPoint(clientX, clientY);
    for (const el of els) {
      const port = (el as HTMLElement).closest?.('[data-port="input"]') as HTMLElement | null;
      if (port) {
        const stepId = port.dataset.stepId ?? '';
        const paramName = port.dataset.paramName ?? '';
        if (stepId && paramName) return { stepId, paramName };
      }
    }
    return null;
  }

  function onPointerUp(e: PointerEvent): void {
    if (!drag) return;
    containerEl?.releasePointerCapture?.(e.pointerId);
    if (drag.kind === 'edge') {
      commitEdgeDrag();
    } else if (drag.kind === 'node') {
      // Persist new position (moved or not — a no-op click already selected on down).
      if (drag.moved) tabState.persist();
    }
    drag = null;
  }

  function commitEdgeDrag(): void {
    if (!drag || drag.kind !== 'edge') return;
    const { sourceStepId, sourceJsonPath, targetStepId, targetParamName } = drag;
    if (!targetStepId || !targetParamName) return;
    const srcIdx = steps.findIndex((s) => s.id === sourceStepId);
    const tgtIdx = steps.findIndex((s) => s.id === targetStepId);
    // Serial semantics: the provider must come before the consumer.
    if (srcIdx < 0 || tgtIdx < 0 || tgtIdx <= srcIdx) return;
    const alias = generateAlias(steps, sourceJsonPath);
    tab.steps = commitEdge(steps, sourceStepId, sourceJsonPath, targetStepId, targetParamName, alias);
    tabState.persist();
    onselectnode(targetStepId);
  }

  function deleteSelectedEdge(): void {
    if (!selectedEdgeId) return;
    const edge = edges.find((e) => e.id === selectedEdgeId);
    if (!edge) return;
    tab.steps = removeEdge(steps, { fromStepId: edge.fromStepId, toStepId: edge.toStepId, alias: edge.alias, paramName: edge.paramName });
    selectedEdgeId = null;
    tabState.persist();
  }

  function onKeyDown(e: KeyboardEvent): void {
    const tag = (document.activeElement?.tagName ?? '').toLowerCase();
    if (tag === 'input' || tag === 'textarea' || tag === 'select') return;
    if (e.key === 'Delete' || e.key === 'Backspace') {
      if (selectedEdgeId) {
        e.preventDefault();
        deleteSelectedEdge();
      }
    }
  }

  // --- wheel zoom (about cursor) --------------------------------------------

  function onWheel(e: WheelEvent): void {
    if (!containerEl) return;
    e.preventDefault();
    const rect = containerEl.getBoundingClientRect();
    const cursor = { x: e.clientX - rect.left, y: e.clientY - rect.top };
    const newZoom = clampZoom(zoom * (e.deltaY < 0 ? 1.1 : 1 / 1.1));
    pan = zoomAboutCursor(zoom, newZoom, cursor, pan);
    zoom = newZoom;
  }

  // --- Explorer drag-onto-canvas (HTML5 DnD from ControllerTree) -------------

  function onDragOver(e: DragEvent): void {
    if (Array.from(e.dataTransfer?.types ?? []).includes('application/sleipnir-method')) {
      e.preventDefault();
      if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
    }
  }

  function onDrop(e: DragEvent): void {
    const raw = e.dataTransfer?.getData('application/sleipnir-method');
    if (!raw) return;
    e.preventDefault();
    try {
      const { controller, method } = JSON.parse(raw) as { controller: string; method: string };
      const c = discoveryState.data?.controllers.find((cc) => cc.name === controller);
      const m = c?.methods.find((mm) => mm.methodName === method);
      if (!c || !m) return;
      const rect = containerRect();
      const pos = pointerToCanvas(e.clientX, e.clientY, rect, zoom);
      const id = nextDefaultStepId(steps);
      const step = createStep(c.name, m, discoveryState.data, id);
      step.x = pos.x - NODE_WIDTH / 2;
      step.y = pos.y - 20;
      tab.steps = [...steps, step];
      tabState.persist();
      onselectnode(id);
    } catch {
      /* ignore malformed drag payload */
    }
  }
</script>

<svelte:window onkeydown={onKeyDown} />

<div
  class="dep-canvas"
  class:dragging={drag?.kind === 'edge'}
  bind:this={containerEl}
  onpointerdown={onBackgroundDown}
  onpointermove={onPointerMove}
  onpointerup={onPointerUp}
  onwheel={onWheel}
  ondragover={onDragOver}
  ondrop={onDrop}
  role="presentation"
>
  <DepEdgeLayer {transform} edges={renderEdges} {pending} {selectedEdgeId} dragging={drag?.kind === 'edge'} onselectedge={(id) => (selectedEdgeId = id)} />

  <div class="node-layer" style:transform={transform}>
    {#each steps as step, i (step.id)}
      {@const mm = methodMetaFor(step, discoveryState.data)}
      <DepNode
        step={step}
        index={i}
        pos={positions.get(step.id) ?? { x: 28, y: 28 }}
        selected={step.id === selectedNodeId}
        methodMeta={mm}
        aliasProviders={aliasProvidersFor(i)}
        discovery={discoveryState.data}
        dragTargetParam={drag?.kind === 'edge' && drag.targetStepId === step.id ? drag.targetParamName : null}
        onselect={() => onselectnode(step.id)}
        onstartnodedrag={(e) => onStartNodeDrag(step.id, e)}
        onportdragstart={onPortDragStart}
        onremove={() => {
          if (!tab.steps) return;
          tab.steps = tab.steps.filter((s) => s.id !== step.id);
          if (selectedNodeId === step.id) onselectnode(null);
          tabState.persist();
        }}
      />
    {/each}
  </div>

  {#if steps.length === 0}
    <div class="canvas-empty">
      <p>Noch keine Aufrufe.</p>
      <p class="hint">„+ Aufruf" in der Toolbar klicken oder eine Methode aus dem Explorer auf den Canvas ziehen.</p>
    </div>
  {/if}
</div>

<style>
  .dep-canvas {
    position: relative;
    flex: 1;
    min-height: 0;
    overflow: hidden;
    background:
      radial-gradient(circle, var(--border-muted) 1px, transparent 1px) 0 0 / 22px 22px,
      var(--bg);
    cursor: grab;
    touch-action: none;
  }
  .dep-canvas:active {
    cursor: grabbing;
  }
  .dep-canvas.dragging {
    cursor: crosshair;
    user-select: none;
  }
  .node-layer {
    position: absolute;
    inset: 0;
    transform-origin: 0 0;
    z-index: 2;
    pointer-events: none; /* let nodes opt back in via their own pointer-events */
  }
  .node-layer :global(.dep-node) {
    pointer-events: auto;
  }
  .canvas-empty {
    position: absolute;
    inset: 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 4px;
    color: var(--text-muted);
    pointer-events: none;
    text-align: center;
  }
  .canvas-empty .hint {
    font-size: 0.8rem;
    color: var(--text-dim);
    max-width: 320px;
  }
</style>