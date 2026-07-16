<script lang="ts">
  import type { TrameRequest } from 'trame-client';

  let { requests = [] }: { requests: TrameRequest[] } = $props();

  // Extrahiert @alias-Referenzen aus dem nativen `params`-Array. Ein Alias ist
  // ein String-Wert in `data`, der mit '@' beginnt (z. B. "@newId").
  function extractAliases(params: { data?: unknown }[] | null | undefined): string[] {
    if (!params || !Array.isArray(params)) return [];
    const aliases: string[] = [];
    for (const entry of params) {
      const d = entry?.data;
      if (typeof d === 'string' && d.startsWith('@')) {
        // Alias-Namen: alphanumerisch + Unterstrich nach dem '@'.
        const m = d.slice(1).match(/^[A-Za-z0-9_]+/);
        if (m) aliases.push(m[0]);
      }
    }
    return aliases;
  }

  // Build dependency graph data
  let graph = $derived.by(() => {
    const nodes = requests.map((req, i) => ({
      id: req.id || `req-${i}`,
      label: `${req.controller}.${req.method}` || `Request ${i + 1}`,
      provides: req.dependencyMapping ? Object.keys(req.dependencyMapping) : [],
      dependsOn: extractAliases(req.params),
    }));

    // Resolve edges
    const edges: { from: string; to: string; alias: string }[] = [];
    for (const node of nodes) {
      for (const dep of node.dependsOn) {
        const provider = nodes.find((n) => n.provides.includes(dep));
        if (provider && provider.id !== node.id) {
          edges.push({ from: provider.id, to: node.id, alias: dep });
        }
      }
    }

    return { nodes, edges };
  });
</script>

<div class="dep-graph">
  <span class="field-label">Dependency Graph</span>
  {#if graph.edges.length === 0}
    <div class="empty">No dependencies detected. Use <code>@alias</code> in parameters and <code>DependencyMapping</code> to chain requests.</div>
  {:else}
    <div class="graph-viz">
      {#each graph.nodes as node (node.id)}
        <div class="graph-node">
          <div class="node-label">{node.label}</div>
          <div class="node-id">{node.id}</div>
          {#if node.provides.length > 0}
            <div class="node-provides">
              Exposes: {node.provides.map(a => `@${a}`).join(', ')}
            </div>
          {/if}
        </div>
      {/each}
      {#each graph.edges as edge (edge.from + edge.to)}
        <div class="graph-edge">
          <span class="edge-alias">@{edge.alias}</span>
        </div>
      {/each}
    </div>
  {/if}
</div>

<style>
  .dep-graph {
    flex-shrink: 0;
  }
  .empty {
    padding: 12px;
    text-align: center;
    color: var(--text-muted);
    font-size: 0.82rem;
    border: 1px dashed var(--border);
    border-radius: var(--radius-sm);
  }
  .empty code {
    font-family: var(--font-mono);
    font-size: 0.8rem;
    color: var(--accent-secondary);
  }
  .graph-viz {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    padding: 8px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
  }
  .graph-node {
    padding: 6px 10px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-elevated);
    font-size: 0.8rem;
  }
  .node-label {
    font-weight: 600;
  }
  .node-id {
    font-size: 0.7rem;
    color: var(--text-dim);
    font-family: var(--font-mono);
  }
  .node-provides {
    font-size: 0.7rem;
    color: var(--success);
    margin-top: 2px;
  }
  .graph-edge {
    display: flex;
    align-items: center;
    padding: 4px 8px;
    font-size: 0.75rem;
    color: var(--accent-secondary);
  }
  .edge-alias {
    font-family: var(--font-mono);
  }
</style>
