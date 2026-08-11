<script lang="ts">
  import { discoveryState } from '../../state/discovery.svelte.ts';
  import type { TypeMeta, PropertyMeta } from 'sleipnir-client';
  import { displayType } from '../../utils/params';

  let expandedTypes = $state<Set<string>>(new Set());

  function toggleType(typeName: string) {
    if (expandedTypes.has(typeName)) {
      expandedTypes.delete(typeName);
    } else {
      expandedTypes.add(typeName);
    }
    expandedTypes = new Set(expandedTypes);
  }

  function getTypes(): [string, TypeMeta][] {
    if (!discoveryState.data?.types) return [];
    return Object.entries(discoveryState.data.types);
  }
</script>

<div class="tree-container">
  {#each getTypes() as [typeName, typeMeta] (typeName)}
    <div class="type-group">
      <button class="node type" onclick={() => toggleType(typeName)}>
        <svg
          width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
          class:rotated={expandedTypes.has(typeName)}
          style="transition: transform 0.15s ease;"
        >
          <polyline points="9 18 15 12 9 6"></polyline>
        </svg>
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"></path>
          <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z"></path>
        </svg>
        <span class="type-name">{typeName.split('.').pop()}</span>
      </button>
      {#if expandedTypes.has(typeName)}
        <div class="type-properties">
          {#each typeMeta.properties as prop (prop.propertyName)}
            <div class="node property">
              <span class="prop-name">{prop.propertyName}</span>
              <span class="prop-type">{displayType(prop.propertyType)}</span>
            </div>
          {/each}
        </div>
      {/if}
    </div>
  {/each}
  {#if getTypes().length === 0}
    <div class="empty">No types discovered</div>
  {/if}
</div>

<style>
  .tree-container {
    flex: 1;
    overflow-y: auto;
    min-height: 0;
  }
  .type-group {
    margin-bottom: 1px;
  }
  .node {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 8px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: 0.82rem;
    width: 100%;
    border: none;
    background: transparent;
    color: var(--text-muted);
    text-align: left;
    transition: background 0.1s ease;
  }
  .node:hover {
    background: var(--bg-overlay);
  }
  .node.type {
    font-weight: 500;
  }
  .node svg {
    flex-shrink: 0;
  }
  .prop-name,
  .prop-type {
    min-width: 0;
    flex-shrink: 1;
  }
  .type-name {
    color: var(--text);
  }
  .rotated {
    transform: rotate(90deg);
  }
  .type-properties {
    padding-left: 20px;
    border-left: 1px solid var(--border-muted);
    margin-left: 11px;
  }
  .node.property {
    cursor: default;
    font-size: 0.8rem;
  }
  .prop-name {
    color: var(--text);
  }
  .prop-type {
    margin-left: auto;
    color: var(--text-dim);
    font-size: 0.75rem;
  }
  .empty {
    padding: 16px;
    text-align: center;
    color: var(--text-muted);
    font-size: 0.85rem;
  }
</style>
