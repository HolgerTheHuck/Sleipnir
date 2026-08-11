<script lang="ts">
  import { historyState, type HistoryEntry } from '../../state/history.svelte.ts';
  import { tabState } from '../../state/tabs.svelte.ts';
  import { formatJson } from '../../utils/json';

  function loadEntry(entry: HistoryEntry) {
    tabState.createTab({
      title: `${entry.request.controller}.${entry.request.method}`,
      requestText: entry.request.params ? JSON.stringify(entry.request.params) : '[]',
      responseText: entry.response ? formatJson(entry.response.data ?? entry.response) : '{}',
      status: entry.response ? String(entry.response.code) : 'Error',
      respIdText: entry.response?.id ?? '-',
      duration: entry.duration,
      log: entry.error ?? '',
      params: [],
    });
    historyState.toggle();
  }

  function formatTime(ts: number): string {
    const d = new Date(ts);
    return d.toLocaleTimeString();
  }
</script>

{#if historyState.isOpen}
  <div class="history-panel">
    <div class="history-header">
      <span class="label">History ({historyState.entries.length})</span>
      <div class="actions">
        <button class="ghost small" onclick={() => historyState.clearHistory()}>Clear All</button>
        <button class="ghost small icon" onclick={() => historyState.toggle()} title="Close">
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <line x1="18" y1="6" x2="6" y2="18"></line>
            <line x1="6" y1="6" x2="18" y2="18"></line>
          </svg>
        </button>
      </div>
    </div>

    <div class="history-list">
      {#each historyState.entries as entry (entry.id)}
        <div class="history-item" onclick={() => loadEntry(entry)} onkeydown={(e) => { if (e.key === 'Enter') loadEntry(entry); }} role="button" tabindex="0">
          <div class="item-top">
            <span class="item-method">
              {entry.request.controller}.{entry.request.method}
            </span>
            <span class="item-time">{formatTime(entry.timestamp)}</span>
          </div>
          <div class="item-bottom">
            {#if entry.response}
              <span
                class="pill"
                class:success={entry.response.code >= 200 && entry.response.code < 300}
                class:error={entry.response.code >= 400}
              >
                {entry.response.code}
              </span>
            {:else}
              <span class="pill error">Error</span>
            {/if}
            <span class="item-duration">{entry.duration}</span>
          </div>
          <button
            class="ghost small icon item-remove"
            onclick={(e: MouseEvent) => { e.stopPropagation(); historyState.removeEntry(entry.id); }}
            title="Remove"
          >
            <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <line x1="18" y1="6" x2="6" y2="18"></line>
              <line x1="6" y1="6" x2="18" y2="18"></line>
            </svg>
          </button>
        </div>
      {/each}
      {#if historyState.entries.length === 0}
        <div class="empty">No requests yet. Run a request to see it here.</div>
      {/if}
    </div>
  </div>
{/if}

<style>
  .history-panel {
    border-top: 1px solid var(--border);
    background: var(--bg-elevated);
    max-height: 200px;
    display: flex;
    flex-direction: column;
  }
  .history-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 8px 12px;
    border-bottom: 1px solid var(--border-muted);
    flex-shrink: 0;
  }
  .history-header .label {
    font-weight: 600;
    font-size: 0.8rem;
    text-transform: uppercase;
    letter-spacing: 0.5px;
    color: var(--text-muted);
  }
  .history-list {
    flex: 1;
    overflow-y: auto;
    padding: 4px;
  }
  .history-item {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 8px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    width: 100%;
    border: none;
    background: transparent;
    color: var(--text);
    text-align: left;
    font-size: 0.82rem;
    transition: background 0.1s ease;
  }
  .history-item:hover {
    background: var(--bg-overlay);
  }
  .item-top {
    display: flex;
    gap: 8px;
    flex: 1;
    min-width: 0;
  }
  .item-method {
    font-weight: 500;
    font-family: var(--font-mono);
    font-size: 0.8rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .item-time {
    color: var(--text-dim);
    font-size: 0.75rem;
    flex-shrink: 0;
  }
  .item-bottom {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-shrink: 0;
  }
  .item-duration {
    font-size: 0.75rem;
    color: var(--text-dim);
  }
  .item-remove {
    opacity: 0;
    flex-shrink: 0;
  }
  .history-item:hover .item-remove {
    opacity: 1;
  }
  .empty {
    padding: 16px;
    text-align: center;
    color: var(--text-muted);
    font-size: 0.85rem;
  }
</style>
