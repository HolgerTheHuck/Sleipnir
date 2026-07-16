<script lang="ts">
  import { tabState } from '../../state/tabs.svelte.ts';

</script>

<div class="tab-bar">
  <div class="tabs">
    {#each tabState.tabs as tab (tab.id)}
      <div
        class="tab"
        class:active={tab.id === tabState.activeTabId}
        onclick={() => tabState.switchTab(tab.id)}
        onkeydown={(e) => { if (e.key === 'Enter') tabState.switchTab(tab.id); }}
        role="button"
        tabindex="0"
        title={tab.title}
      >
        {#if tab.type === 'codegen'}
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <polyline points="16 18 22 12 16 6"></polyline>
            <polyline points="8 6 2 12 8 18"></polyline>
          </svg>
        {:else if tab.type === 'dependency'}
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <circle cx="6" cy="6" r="2"></circle>
            <circle cx="18" cy="6" r="2"></circle>
            <circle cx="12" cy="18" r="2"></circle>
            <line x1="7.41" y1="7.41" x2="10.59" y2="14.59"></line>
            <line x1="16.59" y1="7.41" x2="13.41" y2="14.59"></line>
          </svg>
        {/if}
        <span class="tab-title">{tab.title}</span>
        {#if tabState.tabs.length > 1}
          <button
            class="tab-close"
            onclick={(e: MouseEvent) => { e.stopPropagation(); tabState.closeTab(tab.id); }}
            title="Close tab"
          >×</button>
        {/if}
      </div>
    {/each}
  </div>
  <button class="ghost small tab-add" onclick={() => tabState.createTab()} title="New tab">+</button>
</div>

<style>
  .tab-bar {
    display: flex;
    align-items: center;
    gap: 2px;
    margin-bottom: 8px;
    flex-shrink: 0;
    overflow-x: auto;
  }
  .tabs {
    display: flex;
    gap: 2px;
    flex: 1;
    overflow-x: auto;
  }
  .tab {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 5px 10px;
    border-radius: var(--radius-sm) var(--radius-sm) 0 0;
    background: transparent;
    border: 1px solid transparent;
    border-bottom: 2px solid transparent;
    cursor: pointer;
    color: var(--text-muted);
    font-size: 0.82rem;
    white-space: nowrap;
    transition: all 0.1s ease;
  }
  .tab:hover {
    background: var(--bg-overlay);
    color: var(--text);
  }
  .tab.active {
    background: var(--bg);
    border-color: var(--border);
    border-bottom-color: var(--accent);
    color: var(--text);
  }
  .tab-title {
    max-width: 160px;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .tab-close {
    font-weight: 700;
    opacity: 0.5;
    font-size: 1rem;
    line-height: 1;
    padding: 0 2px;
    border: none;
    background: transparent;
    color: inherit;
    cursor: pointer;
    border-radius: 2px;
  }
  .tab-close:hover {
    opacity: 1;
    color: var(--error);
    background: transparent;
  }
  .tab-add {
    flex-shrink: 0;
  }
</style>
