<script lang="ts" module>
  // Maximum descent depth — guards self-referential schemas (e.g. a tree-node
  // type whose children are the same type). Mirrors defaultValueForRef's depth cap.
  export const MAX_SCHEMA_DEPTH = 6;
</script>

<script lang="ts">
  // Self-recursive return-schema tree of draggable source ports for a dependency
  // node. Each row is a drag source: pointerdown → onportdragstart(path, el, event)
  // → the canvas (DepCanvas) starts a pending edge and captures the pointer.
  //
  // JsonPath accumulates as we descend (root '$', property '$.camel', array
  // '$[0]' / '$[*]'), using toCamelCase so paths are identical to those the
  // existing Expose input / jsonPathSuggestions produce — no validation drift
  // against dependencyCheck (which compares against camelCase wire names).
  //
  // Expansion is a per-instance `$state(false)` boolean (NOT a shared Set): a
  // shared Set across a recursive component would re-render the whole tree on
  // toggle and lose focus/scroll state in deeper levels (Svelte 5 does not
  // deeply proxy Set in $state).

  import type { TypeShape } from 'sleipnir-codegen';
  import type { DiscoveryInfo } from 'sleipnir-client';
  import { propertyShape } from 'sleipnir-codegen';
  import { toCamelCase } from '../../utils/params';

  let {
    shape,
    path,
    depth = 0,
    stepId,
    discovery,
    onportdragstart,
  }: {
    shape: TypeShape;
    path: string;
    depth?: number;
    stepId: string;
    discovery: DiscoveryInfo | null;
    onportdragstart: (path: string, el: HTMLElement, event: PointerEvent) => void;
  } = $props();

  let expanded = $state(false);

  // Display label for the *segment* this row represents (last path part).
  let label = $derived(segmentLabel(path));
  // Type label from the shape.
  let typeLabel = $derived(shape.display ?? shape.kind);

  // Object: expandable into properties (until max depth).
  let isObject = $derived(shape.kind === 'object' && !!shape.typeMeta);
  // Array: offer [0] (single-match → scalar/element) and [*] (multi-match → array).
  let isArray = $derived(shape.kind === 'array' && !!shape.element);
  let isLeaf = $derived(!isObject && !isArray);
  let tooDeep = $derived(depth >= MAX_SCHEMA_DEPTH);
  // Show children only when expandable, expanded, and not past max depth.
  let showChildren = $derived((isObject || isArray) && expanded && !tooDeep);

  function segmentLabel(p: string): string {
    if (p === '$') return '$';
    // last segment after '.' or '['
    const seg = p.split(/[.[]/).filter(Boolean).pop() ?? p;
    return seg.replace(/[\]]/g, '');
  }

  function toggle(e: Event): void {
    e.stopPropagation();
    expanded = !expanded;
  }

  function ondown(e: PointerEvent): void {
    // Left button only; let right-click etc. pass through.
    if (e.button !== 0) return;
    // Stop the event bubbling to the canvas container's onBackgroundDown (pan) —
    // otherwise pan overwrites the edge drag state and "wins" the pointer capture,
    // so a press on a schema field enters move mode instead of starting an edge.
    // Mirrors DepNode.onHeaderDown, which stopsPropagation for the same reason.
    e.stopPropagation();
    e.preventDefault();
    onportdragstart(path, e.currentTarget as HTMLElement, e);
  }

  // Object properties to render when expanded.
  let props = $derived(isObject && shape.typeMeta ? (shape.typeMeta.properties ?? []) : []);
</script>

<div class="schema-row" class:leaf={isLeaf || tooDeep}>
  <!-- Chevron for expandable nodes (object/array); spacer for leaves. -->
  {#if isObject || isArray}
    <button
      class="chevron"
      class:rotated={expanded}
      class:disabled={tooDeep}
      onclick={toggle}
      tabindex={-1}
      aria-label="Aufklappen"
      title={tooDeep ? 'Maximale Tiefe erreicht' : ''}
    >
      <svg width="9" height="9" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <polyline points="9 18 15 12 9 6"></polyline>
      </svg>
    </button>
  {:else}
    <span class="chevron-spacer"></span>
  {/if}

  <!-- Draggable port handle: the whole row is the drag source. -->
  <div
    class="port-handle"
    data-port="output"
    data-step-id={stepId}
    data-jsonpath={path}
    onpointerdown={ondown}
    role="button"
    tabindex={-1}
    title="{path} : {typeLabel}"
  >
    <span class="grip" aria-hidden="true">⣷</span>
    <span class="seg">{label}</span>
  </div>
  <span class="seg-type">{tooDeep ? '…' : typeLabel}</span>
</div>

{#if showChildren}
  <div class="schema-children">
    {#if isObject}
      {#each props as prop (prop.propertyName)}
        {@const childPath = `${path === '$' ? '$' : path}.${toCamelCase(prop.propertyName)}`}
        <SchemaPortTree
          shape={propertyShape(prop, discovery)}
          path={childPath}
          depth={depth + 1}
          {stepId}
          {discovery}
          {onportdragstart}
        />
      {/each}
    {:else if isArray && shape.element}
      <!-- Two virtual children: single element [0] and wildcard [*]. -->
      <SchemaPortTree
        shape={shape.element}
        path={`${path}[0]`}
        depth={depth + 1}
        {stepId}
        {discovery}
        {onportdragstart}
      />
      <SchemaPortTree
        shape={shape.element}
        path={`${path}[*]`}
        depth={depth + 1}
        {stepId}
        {discovery}
        {onportdragstart}
      />
    {/if}
  </div>
{/if}

<style>
  .schema-row {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 2px 4px;
    border-radius: var(--radius-sm);
    min-height: 20px;
  }
  .schema-row.leaf {
    padding-left: 6px;
  }
  .chevron,
  .chevron-spacer {
    width: 14px;
    height: 14px;
    flex-shrink: 0;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    background: transparent;
    border: none;
    color: var(--text-dim);
    padding: 0;
    cursor: pointer;
  }
  .chevron:hover {
    color: var(--text);
  }
  .chevron.rotated {
    transform: rotate(90deg);
  }
  .chevron.disabled {
    opacity: 0.3;
    cursor: not-allowed;
  }
  .port-handle {
    display: flex;
    align-items: center;
    gap: 4px;
    flex: 1;
    min-width: 0;
    cursor: grab;
    user-select: none;
    padding: 1px 4px;
    border-radius: var(--radius-sm);
  }
  .port-handle:hover {
    background: var(--bg-overlay);
  }
  .port-handle:active {
    cursor: grabbing;
  }
  .grip {
    font-size: 0.7rem;
    color: var(--text-dim);
    line-height: 1;
  }
  .port-handle:hover .grip {
    color: var(--accent-secondary);
  }
  .seg {
    font-family: var(--font-mono);
    font-size: 0.78rem;
    color: var(--text);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .seg-type {
    font-family: var(--font-mono);
    font-size: 0.7rem;
    color: var(--text-dim);
    white-space: nowrap;
    flex-shrink: 0;
  }
  .schema-children {
    padding-left: 16px;
    border-left: 1px solid var(--border-muted);
    margin-left: 11px;
  }
</style>