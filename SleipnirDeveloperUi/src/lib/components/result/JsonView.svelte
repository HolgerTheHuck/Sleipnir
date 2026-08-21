<script lang="ts">
  // Entry component: parses a string/object JSON value, owns the shared collapse state,
  // seeds nodes deeper than `maxInitialDepth` as collapsed, and renders either a
  // collapsible tree (JsonNode) or a syntax-highlighted flat fallback (when the payload
  // is not JSON or is too large to render as a tree without freezing).
  import { setContext, untrack } from 'svelte';
  import JsonNode from './JsonNode.svelte';

  let {
    value,
    maxInitialDepth = 2,
    showToolbar = true,
  }: {
    value: unknown;
    maxInitialDepth?: number;
    showToolbar?: boolean;
  } = $props();

  type Parsed = { kind: 'json'; value: unknown } | { kind: 'text'; value: string };

  const parsed = $derived.by<Parsed>(() => {
    if (typeof value === 'string') {
      const s = value.trim();
      if (s === '') return { kind: 'text', value };
      try {
        return { kind: 'json', value: JSON.parse(value) };
      } catch {
        return { kind: 'text', value };
      }
    }
    return { kind: 'json', value };
  });

  interface Analysis {
    containers: string[];
    deep: string[];
    count: number;
  }

  function walk(v: unknown, path: string, depth: number, acc: Analysis): void {
    acc.count++;
    if (v !== null && typeof v === 'object') {
      if (depth >= maxInitialDepth && path !== '$') acc.deep.push(path);
      if (Array.isArray(v)) {
        acc.containers.push(path);
        for (let i = 0; i < v.length; i++) walk(v[i], `${path}[${i}]`, depth + 1, acc);
      } else {
        acc.containers.push(path);
        for (const k of Object.keys(v as Record<string, unknown>)) {
          walk((v as Record<string, unknown>)[k], `${path}.${k}`, depth + 1, acc);
        }
      }
    }
  }

  const analysis = $derived.by<Analysis | null>(() => {
    if (parsed.kind !== 'json') return null;
    const acc: Analysis = { containers: [], deep: [], count: 0 };
    walk(parsed.value, '$', 0, acc);
    return acc;
  });

  const TOO_MANY = 5000;
  const useTree = $derived(analysis !== null && analysis.count <= TOO_MANY);

  /** Flat-fallback tokenizer: splits pretty-printed JSON into colored token spans. */
  function tokenize(json: string): { t: string; c: string }[] {
    const tokens: { t: string; c: string }[] = [];
    const re =
      /("(?:[^"\\]|\\.)*")(\s*:)?|(\btrue\b|\bfalse\b)|(\bnull\b)|(-?\d+\.?\d*(?:[eE][+-]?\d+)?)|([{}[\],])/g;
    let last = 0;
    let m: RegExpExecArray | null;
    while ((m = re.exec(json)) !== null) {
      if (m.index > last) tokens.push({ t: json.slice(last, m.index), c: 'punct' });
      if (m[1] !== undefined) {
        tokens.push({ t: m[1], c: m[2] !== undefined ? 'key' : 'string' });
        if (m[2] !== undefined) tokens.push({ t: m[2], c: 'punct' });
      } else if (m[3] !== undefined) {
        tokens.push({ t: m[3], c: 'bool' });
      } else if (m[4] !== undefined) {
        tokens.push({ t: m[4], c: 'null' });
      } else if (m[5] !== undefined) {
        tokens.push({ t: m[5], c: 'number' });
      } else if (m[6] !== undefined) {
        tokens.push({ t: m[6], c: 'punct' });
      }
      last = re.lastIndex;
    }
    if (last < json.length) tokens.push({ t: json.slice(last), c: 'punct' });
    return tokens;
  }

  const flatTokens = $derived.by<{ t: string; c: string }[] | null>(() => {
    if (parsed.kind !== 'json') return null;
    return tokenize(JSON.stringify(parsed.value, null, 2));
  });

  // Collapse state: a reactive record path -> true. JsonNode reads `collapsed[path]`
  // directly from the context (the most standard Svelte 5 property-read pattern, so
  // membership is tracked and toggle/collapseAll mutations re-render the node).
  let collapsed = $state<Record<string, boolean>>({});

  interface JsonCtx {
    collapsed: Record<string, boolean>;
    toggle: (p: string) => void;
  }

  // Re-seed collapse state when the payload changes (new response = fresh fold).
  // Reads `analysis` (tracked → re-runs on payload change) and writes `collapsed`
  // untracked, so the effect never depends on the state it writes (no write→read loop).
  $effect(() => {
    const a = analysis;
    untrack(() => {
      for (const k of Object.keys(collapsed)) delete collapsed[k];
      if (a) for (const p of a.deep) collapsed[p] = true;
    });
  });

  setContext<JsonCtx>('json-view', {
    collapsed,
    toggle: (p: string) => {
      if (collapsed[p]) delete collapsed[p];
      else collapsed[p] = true;
    },
  });

  function collapseAll() {
    for (const k of Object.keys(collapsed)) delete collapsed[k];
    if (analysis) for (const p of analysis.containers) collapsed[p] = true;
  }
  function expandAll() {
    for (const k of Object.keys(collapsed)) delete collapsed[k];
  }
</script>

{#if parsed.kind === 'text'}
  <pre class="code view"><code>{parsed.value}</code></pre>
{:else if useTree}
  {#if showToolbar}
    <div class="jv-toolbar">
      <button class="ghost small" onclick={collapseAll}>Collapse all</button>
      <button class="ghost small" onclick={expandAll}>Expand all</button>
    </div>
  {/if}
  <div class="jv-tree">
    <JsonNode value={parsed.value} path="$" depth={0} />
  </div>
{:else}
  <pre class="code view">{#each flatTokens ?? [] as tok, i (i)}<span class="tok-{tok.c}">{tok.t}</span>{/each}</pre>
{/if}

<style>
  .jv-toolbar {
    display: flex;
    gap: 6px;
    margin-bottom: 4px;
    flex-shrink: 0;
  }
  .jv-tree {
    flex: 1;
    min-height: 0;
    overflow: auto;
    padding: 10px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    color: var(--code-text);
    font-family: var(--font-mono);
    font-size: 0.85rem;
    line-height: 1.3;
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
  .tok-key {
    color: var(--json-key);
  }
  .tok-string {
    color: var(--json-string);
  }
  .tok-number {
    color: var(--json-number);
  }
  .tok-bool {
    color: var(--json-bool);
  }
  .tok-null {
    color: var(--json-null);
  }
  .tok-punct {
    color: var(--json-punct);
  }
</style>