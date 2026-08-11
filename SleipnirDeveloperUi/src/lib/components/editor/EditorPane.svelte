<script lang="ts">
  import { tabState } from '../../state/tabs.svelte.ts';
  import { historyState } from '../../state/history.svelte.ts';
  import { discoveryState } from '../../state/discovery.svelte.ts';
  import { executeRequest, executeBatch } from '../../api/client';
  import { formatJson } from '../../utils/json';
  import { defaultValueForParam } from '../../utils/params';
  import { ExecutionMode, type SleipnirRequest, type SleipnirParameter } from 'sleipnir-client';
  import TabBar from './TabBar.svelte';
  import ParamEditor from './ParamEditor.svelte';
  import JsonEditor from './JsonEditor.svelte';
  import BatchEditor from './BatchEditor.svelte';
  import DependencyGraph from './DependencyGraph.svelte';
  import CodegenPanel from './CodegenPanel.svelte';
  import CodegenPage from './CodegenPage.svelte';
  import DependencyBuilderPage from './DependencyBuilderPage.svelte';

  // `requestText` ist ein JSON-String für die Anzeige (formatiert zum Editieren);
  // für den Wire-Call muss er in das native `params`-Array geparst werden.
  function safeParseParams(text: string): SleipnirParameter[] {
    try {
      const arr = JSON.parse(text);
      return Array.isArray(arr) ? arr : [];
    } catch {
      return [];
    }
  }

  let isBatch = $state(false);
  let batchRequests = $state<SleipnirRequest[]>([]);
  let batchMode = $state(ExecutionMode.Parallel);
  let running = $state(false);

  function handleRun() {
    if (isBatch) {
      runBatch();
    } else {
      runSingle();
    }
  }

  async function runSingle() {
    const tab = tabState.activeTab;
    if (!tab?.controller || !tab?.method) return;

    running = true;
    const start = performance.now();

    // Sync editor content to params
    tabState.syncParamsFromEditor(tab, tab.requestText);

    const request: SleipnirRequest = {
      controller: tab.controller.name,
      method: tab.method.methodName,
      params: safeParseParams(tab.requestText),
      id: `${tab.controller.name}.${tab.method.methodName}`,
      dependencyMapping: null,
    };

    try {
      const response = await executeRequest(request);
      const duration = `${Math.round(performance.now() - start)} ms`;
      // Ergebnis zentral über tabState.applyResult schreiben → inkl. Persistenz.
      tabState.applyResult(
        tab,
        response,
        duration,
        formatJson(response.data ?? response),
        String(response.code ?? '-'),
        response.isSuccess ? '' : `Error ${response.code}: ${response.error?.message ?? ''}`,
      );

      historyState.addEntry({
        id: `${Date.now()}-${Math.random().toString(16).slice(2, 6)}`,
        timestamp: Date.now(),
        request,
        response,
        duration,
      });
    } catch (err) {
      const duration = `${Math.round(performance.now() - start)} ms`;
      tabState.applyResult(
        tab,
        { id: null },
        duration,
        tab.responseText,
        'Error',
        err instanceof Error ? err.message : String(err),
      );

      historyState.addEntry({
        id: `${Date.now()}-${Math.random().toString(16).slice(2, 6)}`,
        timestamp: Date.now(),
        request,
        response: null,
        duration,
        error: err instanceof Error ? err.message : String(err),
      });
    } finally {
      running = false;
    }
  }

  async function runBatch() {
    running = true;
    const start = performance.now();

    try {
      const responses = await executeBatch({
        requests: batchRequests,
        mode: batchMode,
      });

      const tab = tabState.activeTab;
      if (tab) {
        const duration = `${Math.round(performance.now() - start)} ms`;
        tabState.applyResult(
          tab,
          { id: null },
          duration,
          formatJson(responses),
          'Batch OK',
        );
      }
    } catch (err) {
      const tab = tabState.activeTab;
      if (tab) {
        const duration = `${Math.round(performance.now() - start)} ms`;
        tabState.applyResult(
          tab,
          { id: null },
          duration,
          tab.responseText,
          'Error',
          err instanceof Error ? err.message : String(err),
        );
      }
    } finally {
      running = false;
    }
  }

  function handleFormat() {
    const tab = tabState.activeTab;
    if (!tab) return;
    tab.requestText = formatJson(tab.requestText);
  }

  function handleReset() {
    const tab = tabState.activeTab;
    if (!tab) return;
    if (tab.controller && tab.method) {
      tab.params = tab.method.parameters.map((p) => ({ ...p, value: defaultValueForParam(p, discoveryState.data) }));
      tabState.syncRequestFromParams(tab);
    } else {
      tab.requestText = '[]';
      tab.params = [];
    }
    tab.responseText = '{}';
    tab.log = '';
    tab.status = '-';
    tab.respIdText = '-';
    tab.duration = '-- ms';
    tabState.persist();
  }

  function onEditorChange(newValue: string) {
    const tab = tabState.activeTab;
    if (tab) {
      tab.requestText = newValue;
    }
  }
</script>

<div class="editor">
  <TabBar />

  {#if tabState.activeTab}
    {#if tabState.activeTab.type === 'codegen'}
      <CodegenPage />
    {:else if tabState.activeTab.type === 'dependency'}
      <DependencyBuilderPage tab={tabState.activeTab} />
    {:else}
      <div class="toolbar">
        {#if tabState.activeTab.controller && tabState.activeTab.method}
          <span class="pill accent">{tabState.activeTab.controller.name}.{tabState.activeTab.method.methodName}</span>
        {:else}
          <span class="pill">No method selected</span>
        {/if}
        <span class="pill">{tabState.activeTab.params.length} params</span>
        <span class="pill">{tabState.activeTab.duration}</span>

        <div class="toolbar-actions">
          <label class="batch-toggle">
            <input type="checkbox" bind:checked={isBatch} />
            <span>Batch</span>
          </label>
          <button class="ghost small" onclick={handleFormat}>Format</button>
          <button class="ghost small" onclick={handleReset}>Reset</button>
          <button class="primary" onclick={handleRun} disabled={running}>
            {running ? 'Running...' : 'Run'}
          </button>
        </div>
      </div>

      <div class="editor-scroll">
        {#if isBatch}
          <BatchEditor bind:requests={batchRequests} bind:mode={batchMode} />
          {#if batchRequests.length > 0}
            <DependencyGraph requests={batchRequests} />
          {/if}
        {:else}
          <ParamEditor tab={tabState.activeTab} />
          <JsonEditor value={tabState.activeTab.requestText} onchange={onEditorChange} />
          <CodegenPanel tab={tabState.activeTab} />
        {/if}
      </div>
    {/if}
  {:else}
    <div class="empty-state">
      <p>Select a method from the explorer or create a new tab to get started.</p>
    </div>
  {/if}
</div>

<style>
  .editor {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
    overflow: hidden;
  }
  .toolbar {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
    margin-bottom: 8px;
    flex-shrink: 0;
  }
  .toolbar-actions {
    display: flex;
    align-items: center;
    gap: 4px;
    margin-left: auto;
  }
  .batch-toggle {
    display: flex;
    align-items: center;
    gap: 4px;
    font-size: 0.8rem;
    color: var(--text-muted);
    cursor: pointer;
  }
  .editor-scroll {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    display: flex;
    flex-direction: column;
    gap: 8px;
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
