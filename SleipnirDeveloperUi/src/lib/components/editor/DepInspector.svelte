<script lang="ts">
  // Right-hand inspector for the dependency canvas. Wraps `DependencyStep.svelte`
  // *unchanged* (same props as the old step-list used) so the proven per-step editor
  // — controller/method pickers, literal/@alias toggle, expose definition — is reused
  // verbatim. Adds a header with the step id and duplicate/delete actions. The canvas
  // is the primary authoring surface; this pane is for the fine-grained edits a
  // drag gesture can't express (renaming an alias, switching a param back to literal,
  // picking a controller/method for an empty node).

  import type { DepStep } from '../../state/tabs.svelte';
  import type { AliasProvider } from '../../utils/dependencyCheck';
  import DependencyStep from './DependencyStep.svelte';

  let {
    step,
    index,
    availableAliases = [],
    aliasProviders = {},
    onremove,
    onduplicate,
    onchange,
  }: {
    step: DepStep | null;
    index: number;
    availableAliases?: string[];
    aliasProviders?: Record<string, AliasProvider>;
    onremove?: () => void;
    onduplicate?: () => void;
    onchange?: () => void;
  } = $props();
</script>

<div class="dep-inspector">
  {#if step}
    <div class="inspector-header">
      <span class="inspector-title" title="Selektierter Aufruf">Aufruf {index + 1}</span>
      <span class="inspector-id">{step.id || '?'}</span>
      <div class="inspector-actions">
        <button class="ghost small" onclick={() => onduplicate?.()} title="Aufruf duplizieren">Duplizieren</button>
        <button class="ghost small danger" onclick={() => onremove?.()} title="Aufruf entfernen">Löschen</button>
      </div>
    </div>
    <div class="inspector-body">
      <DependencyStep
        {step}
        {index}
        {availableAliases}
        {aliasProviders}
        onchange={onchange}
      />
    </div>
  {:else}
    <div class="inspector-empty">
      <p>Kein Aufruf selektiert.</p>
      <p class="hint">Klicke einen Knoten auf dem Canvas an oder ziehe eine Methode aus dem Explorer hierher.</p>
    </div>
  {/if}
</div>

<style>
  .dep-inspector {
    display: flex;
    flex-direction: column;
    width: 380px;
    flex-shrink: 0;
    min-height: 0;
    border-left: 1px solid var(--border);
    background: var(--bg);
    overflow: hidden;
  }
  .inspector-header {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 12px;
    border-bottom: 1px solid var(--border-muted);
    flex-shrink: 0;
  }
  .inspector-title {
    font-size: 0.85rem;
    font-weight: 600;
  }
  .inspector-id {
    font-family: var(--font-mono);
    font-size: 0.78rem;
    color: var(--text-dim);
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .inspector-actions {
    display: flex;
    gap: 4px;
    flex-shrink: 0;
  }
  .inspector-actions .danger:hover {
    color: var(--error);
  }
  .inspector-body {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    padding: 8px;
  }
  .inspector-empty {
    margin: 24px 16px;
    padding: 24px 16px;
    text-align: center;
    color: var(--text-muted);
    border: 1px dashed var(--border);
    border-radius: var(--radius-sm);
  }
  .inspector-empty p {
    margin: 0;
  }
  .inspector-empty .hint {
    margin-top: 6px;
    font-size: 0.8rem;
    color: var(--text-dim);
  }
</style>