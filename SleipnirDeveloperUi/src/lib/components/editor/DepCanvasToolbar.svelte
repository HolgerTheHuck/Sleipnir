<script lang="ts">
  // Toolbar above the dependency canvas. Stateless affordance bar — every action is
  // delegated to the parent (DependencyBuilderPage), which owns the steps + view-reset
  // signal. „+ Aufruf" adds an empty node and selects it (controller/method chosen in
  // the inspector); the Explorer drag (ControllerTree) is the faster path to a
  // pre-filled node. Mode is locked to Serial (Parallel/topological = Phase 2).

  let {
    stepsCount,
    duration,
    isValid,
    running,
    onadd,
    onrelink,
    onzoomreset,
    onrun,
  }: {
    stepsCount: number;
    duration: string;
    isValid: boolean;
    running: boolean;
    onadd: () => void;
    onrelink: () => void;
    onzoomreset: () => void;
    onrun: () => void;
  } = $props();
</script>

<div class="dep-toolbar">
  <span class="pill accent">Dependency Builder</span>
  <span class="dep-hint">@alias-Ketten visuell zusammenstellen → Code + Ausführung</span>
  <span class="pill locked" title="Serial-Modus zwingend für @alias-Auflösung (Parallel/topological = Phase 2)">Mode: Serial (locked)</span>
  <span class="pill">{stepsCount} Aufruf{stepsCount === 1 ? '' : 'e'}</span>
  {#if duration}
    <span class="pill">{duration}</span>
  {/if}
  <div class="toolbar-actions">
    <button class="ghost small" onclick={onadd} title="Leeren Aufruf hinzufügen (Controller/Methode im Inspector wählen)">+ Aufruf</button>
    <button class="ghost small" onclick={onrelink} title="Alle Knoten neu anordnen (topologisch) + Ansicht zurücksetzen" disabled={stepsCount === 0}>Neu anordnen</button>
    <button class="ghost small" onclick={onzoomreset} title="Zoom/Pan zurücksetzen" disabled={stepsCount === 0}>Ansicht</button>
    <button class="primary small" onclick={onrun} disabled={!isValid || running}>
      {running ? 'Running…' : 'Ausführen'}
    </button>
  </div>
</div>

<style>
  .dep-toolbar {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
    flex-shrink: 0;
    padding-bottom: 8px;
  }
  .dep-hint {
    font-size: 0.78rem;
    color: var(--text-muted);
  }
  .pill.locked {
    color: var(--text-dim);
    border-style: dashed;
  }
  .toolbar-actions {
    display: flex;
    align-items: center;
    gap: 4px;
    margin-left: auto;
  }
</style>