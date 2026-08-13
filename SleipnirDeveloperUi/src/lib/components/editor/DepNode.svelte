<script lang="ts">
  // One call node on the dependency canvas. Header (draggable to move the node),
  // left input ports (parameter drop targets — `data-port="input"`), right output
  // ports (committed exposes, display-only in Phase 1), and a collapsible return-
  // schema tree of drag sources (SchemaPortTree). Port severity reuses the same
  // dependencyCheck calls as DependencyStep.svelte, so node colors and the bottom
  // type-check box never disagree.

  import type { DiscoveryInfo, MethodMeta } from 'sleipnir-client';
  import { returnShape } from 'sleipnir-codegen';
  import type { DepStep, DepParam, DepExpose } from '../../state/tabs.svelte';
  import type { Point } from '../../utils/canvasViewport';
  import { displayType } from '../../utils/params';
  import { checkExpose, checkAliasBinding, type AliasProvider, type CheckIssue } from '../../utils/dependencyCheck';
  import SchemaPortTree from './SchemaPortTree.svelte';

  let {
    step,
    index,
    pos,
    selected,
    methodMeta,
    aliasProviders,
    discovery,
    dragTargetParam = null,
    onselect,
    onstartnodedrag,
    onportdragstart,
    onremove,
  }: {
    step: DepStep;
    index: number;
    pos: Point;
    selected: boolean;
    methodMeta: MethodMeta | null;
    aliasProviders: Record<string, AliasProvider>;
    discovery: DiscoveryInfo | null;
    /** Parameter name currently hovered as a drop target (for highlight). */
    dragTargetParam?: string | null;
    onselect: () => void;
    onstartnodedrag: (event: PointerEvent) => void;
    onportdragstart: (path: string, el: HTMLElement, event: PointerEvent) => void;
    onremove: () => void;
  } = $props();

  let showSchema = $state(false);

  let retShape = $derived(methodMeta ? returnShape(methodMeta, discovery) : null);
  let hasSchema = $derived(!!retShape);

  function exposeIssue(ex: DepExpose): CheckIssue | null {
    if (!ex.alias || !ex.jsonPath) return null;
    return checkExpose(index, step.id, methodMeta, ex.jsonPath, discovery);
  }

  function inputIssue(p: DepParam): CheckIssue | null {
    if (!p.useAlias || !p.aliasRef) return null;
    const prov = aliasProviders[p.aliasRef];
    if (!prov) return null;
    return checkAliasBinding(index, step.id, prov.methodMeta, prov.jsonPath, p, discovery);
  }

  function onHeaderDown(e: PointerEvent): void {
    if (e.button !== 0) return;
    e.stopPropagation();
    onselect();
    onstartnodedrag(e);
  }

  function toggleSchema(e: Event): void {
    e.stopPropagation();
    showSchema = !showSchema;
  }
</script>

<div
  class="dep-node"
  class:selected
  style:left="{pos.x}px"
  style:top="{pos.y}px"
  data-node-id={step.id}
  role="button"
  tabindex={-1}
>
  <div class="node-header" role="button" tabindex={-1} aria-label="Knoten verschieben" onpointerdown={onHeaderDown}>
    <span class="step-badge" title="Aufruf-Reihenfolge">{index + 1}</span>
    <span class="node-id" title="Schritt-Id (im Inspector editierbar)">{step.id || '?'}</span>
    <span class="node-title" class:placeholder={!step.controller || !step.method}>
      {step.controller && step.method ? `${step.controller}.${step.method}` : 'Controller.Methode wählen'}
    </span>
    {#if methodMeta}
      <span class="node-return" title="Rückgabetyp">{displayType(methodMeta.returnType)}</span>
    {/if}
    <button class="ghost small icon schema-toggle" class:active={showSchema} disabled={!hasSchema} onclick={toggleSchema} title="Return-Schema anzeigen">
      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="6 9 12 15 18 9"></polyline></svg>
    </button>
    <button class="ghost small icon node-remove" onclick={() => onremove()} title="Aufruf entfernen">
      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="18" y1="6" x2="6" y2="18"></line><line x1="6" y1="6" x2="18" y2="18"></line></svg>
    </button>
  </div>

  <div class="node-body">
    <!-- Input ports (left) — drop targets for a drag from a provider schema. -->
    <div class="ports ports-in">
      {#each step.params as p, pi (p.parameterName + pi)}
        {@const iss = inputIssue(p)}
        {@const isTarget = dragTargetParam === p.parameterName}
        <div
          class="port in"
          class:alias={p.useAlias}
          class:err={iss?.severity === 'error'}
          class:warn={iss?.severity === 'warn'}
          class:drop-target={isTarget}
          data-port="input"
          data-step-id={step.id}
          data-param-name={p.parameterName}
          title={iss ? iss.message : displayType(p.parameterType)}
        >
          <span class="port-dot"></span>
          <span class="port-name">{p.parameterName}</span>
          <span class="port-type">{displayType(p.parameterType)}</span>
          {#if p.useAlias}
            <span class="port-alias">@{p.aliasRef}</span>
          {/if}
        </div>
      {/each}
      {#if step.params.length === 0}
        <div class="port-empty">keine Parameter</div>
      {/if}
    </div>

    <!-- Output ports (right) — committed exposes. Display-only in Phase 1. -->
    <div class="ports ports-out">
      {#each step.exposes as ex, ei (ei)}
        {@const iss = exposeIssue(ex)}
        <div
          class="port out"
          class:err={iss?.severity === 'error'}
          class:warn={iss?.severity === 'warn'}
          title={iss ? iss.message : `Expose @${ex.alias}`}
        >
          <span class="port-alias">@{ex.alias}</span>
          <span class="port-path">{ex.jsonPath}</span>
        </div>
      {/each}
    </div>
  </div>

  {#if showSchema && hasSchema && retShape}
    <div class="schema-panel">
      <span class="schema-label">Return-Schema (ziehe auf einen Parameter)</span>
      <SchemaPortTree shape={retShape} path="$" depth={0} stepId={step.id} {discovery} {onportdragstart} />
    </div>
  {/if}
</div>

<style>
  .dep-node {
    position: absolute;
    width: 260px;
    background: var(--bg-elevated);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    box-shadow: var(--shadow-sm);
    user-select: none;
    z-index: 2;
  }
  .dep-node.selected {
    border-color: var(--accent-secondary);
    box-shadow: 0 0 0 2px rgba(88, 166, 255, 0.25), var(--shadow-sm);
  }
  .node-header {
    display: flex;
    align-items: center;
    gap: 6px;
    height: 34px;
    padding: 0 8px;
    border-bottom: 1px solid var(--border-muted);
    cursor: grab;
    border-radius: var(--radius) var(--radius) 0 0;
    background: var(--bg-overlay);
  }
  .node-header:active {
    cursor: grabbing;
  }
  .step-badge {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 20px;
    height: 20px;
    border-radius: 50%;
    background: var(--accent-secondary);
    color: #fff;
    font-size: 0.68rem;
    font-weight: 700;
    flex-shrink: 0;
  }
  .node-id {
    font-family: var(--font-mono);
    font-size: 0.72rem;
    color: var(--text-dim);
    max-width: 70px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    flex-shrink: 0;
  }
  .node-title {
    flex: 1;
    min-width: 0;
    font-size: 0.8rem;
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .node-title.placeholder {
    color: var(--text-dim);
    font-weight: 400;
    font-style: italic;
  }
  .node-return {
    font-family: var(--font-mono);
    font-size: 0.68rem;
    color: var(--text-dim);
    flex-shrink: 0;
    max-width: 80px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .schema-toggle.active {
    color: var(--accent-secondary);
  }
  .node-remove:hover {
    color: var(--error);
  }

  .node-body {
    display: flex;
    gap: 4px;
    padding: 4px 6px;
  }
  .ports {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
  }
  .ports-in {
    align-items: flex-start;
  }
  .ports-out {
    align-items: flex-end;
  }
  .port {
    display: flex;
    align-items: center;
    gap: 4px;
    height: 22px;
    font-size: 0.74rem;
    padding: 0 4px;
    border-radius: var(--radius-sm);
    white-space: nowrap;
  }
  .port.in {
    width: 100%;
    cursor: default;
  }
  .port.in.drop-target {
    background: rgba(88, 166, 255, 0.18);
    outline: 1px dashed var(--accent-secondary);
  }
  .port-dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--text-dim);
    flex-shrink: 0;
  }
  .port.in.alias .port-dot {
    background: var(--accent-secondary);
  }
  .port-name {
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .port-type {
    font-family: var(--font-mono);
    font-size: 0.68rem;
    color: var(--text-dim);
    margin-left: auto;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .port-alias {
    font-family: var(--font-mono);
    font-size: 0.68rem;
    color: var(--accent-secondary);
    flex-shrink: 0;
  }
  .port.out {
    flex-direction: row-reverse;
    color: var(--text-muted);
  }
  .port.out .port-path {
    font-family: var(--font-mono);
    font-size: 0.68rem;
    color: var(--text-dim);
    max-width: 90px;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .port.err {
    box-shadow: inset 2px 0 0 var(--error);
  }
  .port.warn {
    box-shadow: inset 2px 0 0 var(--warning);
  }
  .port-empty {
    font-size: 0.72rem;
    color: var(--text-dim);
    font-style: italic;
    height: 22px;
    display: flex;
    align-items: center;
  }

  .schema-panel {
    border-top: 1px solid var(--border-muted);
    padding: 6px 8px;
    max-height: 220px;
    overflow-y: auto;
  }
  .schema-label {
    display: block;
    font-size: 0.66rem;
    text-transform: uppercase;
    letter-spacing: 0.4px;
    color: var(--text-muted);
    margin-bottom: 4px;
  }
</style>