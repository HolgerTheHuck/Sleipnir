<script lang="ts">
  import type { ParameterMeta } from 'sleipnir-client';
  import type { Tab } from '../../state/tabs.svelte.ts';
  import { tabState } from '../../state/tabs.svelte.ts';
  import { discoveryState } from '../../state/discovery.svelte.ts';
  import { inferValue, isObjectParam, objectPropertyCount, isBoolParam, displayType } from '../../utils/params';

  let { tab }: { tab: Tab } = $props();

  function onParamChange(param: ParameterMeta & { value: unknown }, newValue: string) {
    if (isObjectParam(param.parameterType, discoveryState.data)) {
      try {
        param.value = JSON.parse(newValue);
      } catch {
        param.value = newValue;
      }
    } else {
      param.value = inferValue(newValue, param.parameterType);
    }
    tabState.syncRequestFromParams(tab);
  }

  function getInputValue(param: ParameterMeta & { value: unknown }): string {
    if (isObjectParam(param.parameterType, discoveryState.data)) {
      if (typeof param.value === 'object' && param.value !== null) {
        return JSON.stringify(param.value, null, 2);
      }
      return '{}';
    }
    if (param.value === null || param.value === undefined) return '';
    return String(param.value);
  }
</script>

<div class="param-list">
  {#each tab.params as param (param.parameterName)}
    <div class="param-card">
      <div class="param-header">
        <span class="param-name">{param.parameterName}</span>
        <span class="param-type">{displayType(param.parameterType)}</span>
      </div>
      {#if isObjectParam(param.parameterType, discoveryState.data)}
        <textarea
          class="code param-textarea"
          value={getInputValue(param)}
          oninput={(e) => onParamChange(param, (e.target as HTMLTextAreaElement).value)}
          rows={Math.min(8, objectPropertyCount(param.parameterType, discoveryState.data) + 2)}
        ></textarea>
      {:else if isBoolParam(param.parameterType)}
        <select
          value={String(param.value)}
          onchange={(e) => onParamChange(param, (e.target as HTMLSelectElement).value)}
        >
          <option value="false">false</option>
          <option value="true">true</option>
        </select>
      {:else}
        <input
          type="text"
          placeholder={param.parameterName}
          value={getInputValue(param)}
          oninput={(e) => onParamChange(param, (e.target as HTMLInputElement).value)}
        />
      {/if}
    </div>
  {/each}
  {#if tab.params.length === 0}
    <div class="empty-params">No parameters</div>
  {/if}
</div>

<style>
  .param-list {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
    gap: 8px;
    flex-shrink: 0;
  }
  .param-card {
    padding: 8px 10px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-overlay);
  }
  .param-header {
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    margin-bottom: 6px;
  }
  .param-name {
    font-weight: 600;
    font-size: 0.85rem;
  }
  .param-type {
    font-size: 0.75rem;
    color: var(--text-dim);
    font-family: var(--font-mono);
  }
  .param-card input,
  .param-card select {
    width: 100%;
    font-size: 0.85rem;
  }
  .param-textarea {
    width: 100%;
    min-height: 60px;
    resize: vertical;
    font-size: 0.8rem;
  }
  .empty-params {
    grid-column: 1 / -1;
    padding: 16px;
    text-align: center;
    color: var(--text-muted);
    font-size: 0.85rem;
  }
</style>
