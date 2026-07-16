<script lang="ts">
  import { tabState } from '../../state/tabs.svelte.ts';
  import { formatJson } from '../../utils/json';

  let statusClass = $derived.by(() => {
    const status = tabState.activeTab?.status ?? '';
    if (status === 'Running...') return 'warning';
    const code = parseInt(status);
    if (isNaN(code)) return status === 'Error' ? 'error' : '';
    if (code >= 200 && code < 300) return 'success';
    if (code >= 400) return 'error';
    return '';
  });
</script>

<div class="result">
  <div class="pane-header">
    <span class="label">Result</span>
    {#if tabState.activeTab}
      <span class="pill" class:success={statusClass === 'success'} class:error={statusClass === 'error'} class:warning={statusClass === 'warning'}>
        {tabState.activeTab.status}
      </span>
    {:else}
      <span class="pill">Idle</span>
    {/if}
  </div>

  {#if tabState.activeTab}
    <div class="result-meta">
      <div><span class="meta-label">Status:</span> <span>{tabState.activeTab.status}</span></div>
      <div><span class="meta-label">ID:</span> <span class="code">{tabState.activeTab.respIdText}</span></div>
      <div><span class="meta-label">Duration:</span> <span>{tabState.activeTab.duration}</span></div>
    </div>

    <span class="field-label">Response</span>
    <pre class="code view"><code>{tabState.activeTab.responseText}</code></pre>

    {#if tabState.activeTab.log}
      <span class="field-label">Errors / Info</span>
      <pre class="code view error-log"><code>{tabState.activeTab.log}</code></pre>
    {/if}
  {:else}
    <div class="empty-state">
      <p>Run a request to see the response here.</p>
    </div>
  {/if}
</div>

<style>
  .result {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
    overflow: hidden;
  }
  .result-meta {
    display: flex;
    gap: 16px;
    margin-bottom: 8px;
    font-size: 0.85rem;
    flex-shrink: 0;
  }
  .meta-label {
    font-weight: 600;
    color: var(--text);
  }
  .view {
    flex: 1;
    min-height: 0;
    overflow: auto;
    white-space: pre-wrap;
    word-break: break-all;
    padding: 10px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    color: var(--code-text);
    font-family: var(--font-mono);
    font-size: 0.85rem;
    line-height: 1.5;
    margin: 0;
  }
  .error-log {
    color: var(--error);
    max-height: 120px;
  }
  .empty-state {
    flex: 1;
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--text-muted);
    font-size: 0.9rem;
  }
</style>
