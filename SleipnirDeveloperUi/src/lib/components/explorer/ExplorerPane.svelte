<script lang="ts">
  import { discoveryState } from '../../state/discovery.svelte.ts';
  import { layoutState } from '../../state/layout.svelte.ts';
  import ControllerTree from './ControllerTree.svelte';
  import TypesTree from './TypesTree.svelte';

  let searchQuery = $state(discoveryState.searchQuery);
  $effect(() => {
    discoveryState.searchQuery = searchQuery;
  });

  // Vertikaler Splitter zwischen DISCOVERY (oben) und TYPES (unten). Die
  // Discovery-Höhe ist frei ziehbar, TYPES füllt den Rest. Die Größe liegt im
  // zentralen layoutState (serialisierbar + beim Workspace-Import live).
  const MIN_DISCOVERY = 140;
  const MIN_TYPES = 120;

  let explorerEl = $state<HTMLDivElement | null>(null);
  let dragging = $state(false);

  function onSplitterDown(e: MouseEvent) {
    e.preventDefault();
    dragging = true;
    const startY = e.clientY;
    const startH = layoutState.discoveryHeight;
    const containerH = explorerEl?.getBoundingClientRect().height ?? 600;

    function onMove(ev: MouseEvent) {
      const dy = ev.clientY - startY;
      layoutState.discoveryHeight = Math.max(MIN_DISCOVERY, Math.min(startH + dy, containerH - MIN_TYPES));
    }

    function onUp() {
      dragging = false;
      // Erst am Drag-Ende persistieren — nicht pro Mausbewegung.
      layoutState.persist();
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    }

    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }
</script>

<div class="explorer" bind:this={explorerEl} class:is-dragging={dragging}>
  <div class="section discovery-section" style="height: {layoutState.discoveryHeight}px">
    <div class="pane-header">
      <span class="label">Discovery</span>
      {#if discoveryState.loading}
        <span class="hint">Loading...</span>
      {:else if discoveryState.error}
        <span class="hint error-text">{discoveryState.error}</span>
      {:else}
        <span class="hint">
          {discoveryState.data?.controllers.length ?? 0} controllers
        </span>
      {/if}
    </div>

    <div class="search-box">
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="search-icon">
        <circle cx="11" cy="11" r="8"></circle>
        <line x1="21" y1="21" x2="16.65" y2="16.65"></line>
      </svg>
      <input
        type="search"
        placeholder="Filter controllers or methods..."
        bind:value={searchQuery}
      />
    </div>

    <div class="tree-wrap">
      <ControllerTree />
    </div>
  </div>

  <div
    class="h-splitter"
    onmousedown={onSplitterDown}
    role="separator"
    tabindex="-1"
    aria-label="Resize discovery and types"
  ></div>

  <div class="section types-section">
    <div class="pane-header compact">
      <span class="label">Types</span>
      <span class="hint">
        {discoveryState.data?.types ? Object.keys(discoveryState.data.types).length : 0} types
      </span>
    </div>

    <div class="tree-wrap">
      <TypesTree />
    </div>
  </div>
</div>

<style>
  .explorer {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
    overflow: hidden;
  }
  .explorer.is-dragging {
    cursor: row-resize;
    user-select: none;
  }
  .section {
    display: flex;
    flex-direction: column;
    min-height: 0;
    overflow: hidden;
  }
  .discovery-section {
    flex-shrink: 0;
  }
  .types-section {
    flex: 1;
  }
  .tree-wrap {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-height: 0;
    overflow: hidden;
  }
  .search-box {
    position: relative;
    margin-bottom: 8px;
    flex-shrink: 0;
  }
  .search-box input {
    width: 100%;
    padding-left: 30px;
  }
  .search-icon {
    position: absolute;
    left: 8px;
    top: 50%;
    transform: translateY(-50%);
    color: var(--text-dim);
    pointer-events: none;
  }
  .compact {
    margin-top: 4px;
  }
  .error-text {
    color: var(--error);
  }
  .h-splitter {
    height: 9px;
    cursor: row-resize;
    background: var(--border-muted);
    flex-shrink: 0;
    transition: background 0.15s ease;
    position: relative;
    z-index: 10;
    margin: 4px 0;
    border-radius: 999px;
    display: flex;
    align-items: center;
    justify-content: center;
  }
  .h-splitter::after {
    content: '';
    width: 28px;
    height: 3px;
    border-radius: 999px;
    background: var(--text-dim);
    opacity: 0.5;
    transition: opacity 0.15s ease, background 0.15s ease;
  }
  .h-splitter:hover::after,
  .is-dragging .h-splitter::after {
    background: var(--accent-secondary);
    opacity: 0.9;
  }
</style>