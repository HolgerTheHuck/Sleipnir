<script lang="ts">
  import { discoveryState } from '../../state/discovery.svelte.ts';
  import { tabState } from '../../state/tabs.svelte.ts';
  import type { ControllerMeta, MethodMeta } from 'sleipnir-client';

  let expandedControllers = $state<Set<string>>(new Set());

  function toggleController(name: string) {
    if (expandedControllers.has(name)) {
      expandedControllers.delete(name);
    } else {
      expandedControllers.add(name);
    }
    expandedControllers = new Set(expandedControllers);
  }

  function isExpanded(controller: ControllerMeta): boolean {
    // Bei aktiver Suche werden Treffer immer ausgeklappt, damit die Ergebnisse
    // sichtbar bleiben — sonst wäre der Filter hinter einem zugeklappten Knoten
    // versteckt und wirkt kaputt.
    if (discoveryState.searchQuery.trim()) return true;
    return expandedControllers.has(controller.name);
  }

  function isMethodActive(controller: ControllerMeta, method: MethodMeta): boolean {
    return tabState.activeTab?.controller?.name === controller.name && tabState.activeTab?.method?.methodName === method.methodName;
  }

  /** HTML5-Drag auf den Dependency-Canvas: trägt {controller, method} als
   *  `application/sleipnir-method`-Payload. DepCanvas.onDrop erzeugt daraus einen
   *  Aufruf-Knoten. Klick (ohne Drag) öffnet weiterhin den Einzelmethoden-Tab. */
  function onMethodDragStart(controller: ControllerMeta, method: MethodMeta, e: DragEvent): void {
    const payload = JSON.stringify({ controller: controller.name, method: method.methodName });
    if (e.dataTransfer) {
      e.dataTransfer.setData('application/sleipnir-method', payload);
      e.dataTransfer.effectAllowed = 'copy';
    }
  }
</script>

<div class="tree-container">
  {#if discoveryState.filteredControllers.length === 0 && discoveryState.searchQuery}
    <div class="empty">No results for "{discoveryState.searchQuery}"</div>
  {:else}
    {#each discoveryState.filteredControllers as controller (controller.name)}
      <div class="tree-group">
        <button class="node controller" onclick={() => toggleController(controller.name)}>
          <svg
            width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
            class:rotated={isExpanded(controller)}
            style="transition: transform 0.15s ease;"
          >
            <polyline points="9 18 15 12 9 6"></polyline>
          </svg>
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"></path>
          </svg>
          <span>{controller.name}</span>
          <span class="badge">{controller.methods.length}</span>
        </button>
        {#if isExpanded(controller)}
          <div class="controller-methods">
            {#each controller.methods as method (method.methodName)}
              <button
                class="node method"
                class:active={isMethodActive(controller, method)}
                draggable="true"
                ondragstart={(e) => onMethodDragStart(controller, method, e)}
                onclick={() => tabState.openMethodTab(controller, method)}
                title="{method.methodName} — Klick öffnet den Tab, Drag auf den Dependency-Canvas erzeugt einen Aufruf"
              >
                <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"></polygon>
                </svg>
                <span class="method-name">{method.methodName}</span>
                <span class="params-preview">
                  ({method.parameters.map(p => p.parameterName).join(', ')})
                </span>
              </button>
            {/each}
          </div>
        {/if}
      </div>
    {/each}
  {/if}
</div>

<style>
  .tree-container {
    flex: 1;
    overflow-y: auto;
    min-height: 0;
  }
  .tree-group {
    margin-bottom: 2px;
  }
  .node {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 5px 8px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: 0.85rem;
    width: 100%;
    border: none;
    background: transparent;
    color: var(--text);
    text-align: left;
    transition: background 0.1s ease;
  }
  .node:hover {
    background: var(--bg-overlay);
  }
  .node.controller {
    font-weight: 600;
    color: var(--text);
  }
  .rotated {
    transform: rotate(90deg);
  }
  .controller-methods {
    padding-left: 20px;
    border-left: 1px solid var(--border-muted);
    margin-left: 11px;
  }
  .node svg {
    flex-shrink: 0;
  }
  .node.method {
    padding-left: 6px;
    color: var(--text-muted);
    font-size: 0.82rem;
  }
  .method-name,
  .params-preview {
    min-width: 0;
    flex-shrink: 1;
  }
  .node.method.active {
    color: var(--accent-secondary);
    background: rgba(88, 166, 255, 0.08);
    font-weight: 500;
  }
  .badge {
    margin-left: auto;
    font-size: 0.7rem;
    padding: 1px 6px;
    border-radius: 999px;
    background: var(--bg-overlay);
    color: var(--text-dim);
  }
  .method-name {
    font-weight: 500;
    color: inherit;
  }
  .params-preview {
    font-size: 0.75rem;
    color: var(--text-dim);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .empty {
    padding: 16px;
    text-align: center;
    color: var(--text-muted);
    font-size: 0.85rem;
  }
</style>