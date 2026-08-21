<script lang="ts">
  // Recursive JSON tree node. Renders object/array containers with a collapse toggle
  // and scalars with token-colored syntax. Reads the shared collapse state + copy
  // handlers from the JsonView context (keyed 'json-view'). Self-imports for recursion.
  import { getContext } from 'svelte';
  import JsonNode from './JsonNode.svelte';

  let {
    value,
    path,
    key,
    depth = 0,
  }: {
    value: unknown;
    path: string;
    /** Property name for object children (rendered as a colored, click-to-copy key).
     *  undefined for the root and for array elements. */
    key?: string;
    depth?: number;
  } = $props();

  interface JsonCtx {
    collapsed: Record<string, boolean>;
    toggle: (p: string) => void;
  }
  const ctx = getContext<JsonCtx>('json-view');

  const isContainer = $derived(value !== null && typeof value === 'object');
  const isArray = $derived(Array.isArray(value));

  /** Normalized child list: each entry has the child value, its display key (undefined
   *  for array elements), and its JsonPath. */
  const children = $derived.by<{ v: unknown; key: string | undefined; childPath: string }[]>(() => {
    if (!isContainer) return [];
    if (isArray) {
      const arr = value as unknown[];
      return arr.map((v, i) => ({ v, key: undefined, childPath: `${path}[${i}]` }));
    }
    const obj = value as Record<string, unknown>;
    return Object.entries(obj).map(([k, v]) => ({ v, key: k, childPath: `${path}.${k}` }));
  });

  // Read the shared collapse record directly: a property read on a $state record is the
  // canonical tracked read, so toggle/collapseAll mutations in JsonView re-render this node.
  const collapsed = $derived(!!ctx?.collapsed[path]);
  const len = $derived(children.length);

  function toggle() {
    ctx?.toggle(path);
  }

  function copyPath() {
    void navigator.clipboard?.writeText(path);
    flash();
  }

  function copyValue() {
    void navigator.clipboard?.writeText(JSON.stringify(value, null, 2));
    flash();
  }

  let copied = $state(false);
  let flashTimer: ReturnType<typeof setTimeout> | undefined;
  function flash() {
    copied = true;
    clearTimeout(flashTimer);
    flashTimer = setTimeout(() => (copied = false), 1000);
  }

  function scalarInfo(v: unknown): { cls: string; text: string } {
    if (v === null) return { cls: 'null', text: 'null' };
    if (typeof v === 'string') return { cls: 'string', text: JSON.stringify(v) };
    if (typeof v === 'number') return { cls: 'number', text: String(v) };
    if (typeof v === 'boolean') return { cls: 'bool', text: String(v) };
    return { cls: 'null', text: String(v) };
  }

  const scalar = $derived.by(() => scalarInfo(value));
</script>

{#if isContainer}
  <div class="row" style="--depth:{depth}">
    <span class="toggle" onclick={toggle} role="button" tabindex="0"
          aria-label={collapsed ? 'Expand' : 'Collapse'}
          onkeydown={(e) => (e.key === 'Enter' || e.key === ' ') && (e.preventDefault(), toggle())}>
      {collapsed ? '▸' : '▾'}
    </span>
    {#if key !== undefined}
      <span class="key" onclick={copyPath} role="button" tabindex="0" title="Copy path: {path}"
            onkeydown={(e) => (e.key === 'Enter' || e.key === ' ') && (e.preventDefault(), copyPath())}>"{key}"</span>
      <span class="punct">: </span>
    {/if}
    <span class="punct">{isArray ? '[' : '{'}</span>
    {#if collapsed}
      <span class="summary">{isArray ? ` …${len} ` : ` …${len} `}</span>
      <span class="punct">{isArray ? ']' : '}'}</span>
      <button class="copy-val" onclick={copyValue} title="Copy value">{copied ? '✓' : '⧉'}</button>
    {:else}
      <button class="copy-val" onclick={copyValue} title="Copy value">{copied ? '✓' : '⧉'}</button>
    {/if}
  </div>
  {#if !collapsed}
    <div class="children">
      {#each children as child (child.childPath)}
        <JsonNode value={child.v} path={child.childPath} key={child.key} depth={depth + 1} />
      {/each}
    </div>
    <div class="row close" style="--depth:{depth}">
      <span class="toggle-spacer"></span>
      <span class="punct">{isArray ? ']' : '}'}</span>
    </div>
  {/if}
{:else}
  <div class="row" style="--depth:{depth}">
    <span class="toggle-spacer"></span>
    {#if key !== undefined}
      <span class="key" onclick={copyPath} role="button" tabindex="0" title="Copy path: {path}"
            onkeydown={(e) => (e.key === 'Enter' || e.key === ' ') && (e.preventDefault(), copyPath())}>"{key}"</span>
      <span class="punct">: </span>
    {/if}
    <span class="scalar {scalar.cls}">{scalar.text}</span>
    <button class="copy-val" onclick={copyValue} title="Copy value">{copied ? '✓' : '⧉'}</button>
  </div>
{/if}

<style>
  .row {
    display: flex;
    align-items: flex-start;
    padding-left: calc(var(--depth) * 14px);
    line-height: 1.3;
  }
  .row:hover {
    background: var(--json-hover-bg);
  }
  .toggle,
  .toggle-spacer {
    width: 16px;
    flex: 0 0 auto;
    user-select: none;
  }
  .toggle {
    cursor: pointer;
    color: var(--json-toggle);
    text-align: center;
  }
  .key {
    color: var(--json-key);
    cursor: pointer;
  }
  .punct {
    color: var(--json-punct);
  }
  .summary {
    color: var(--text-muted);
    font-style: italic;
  }
  .scalar {
    word-break: break-all;
  }
  .scalar.string {
    color: var(--json-string);
  }
  .scalar.number {
    color: var(--json-number);
  }
  .scalar.bool {
    color: var(--json-bool);
  }
  .scalar.null {
    color: var(--json-null);
  }
  .copy-val {
    margin-left: 6px;
    flex: 0 0 auto;
    padding: 0 4px;
    background: transparent;
    border: none;
    color: var(--text-muted);
    cursor: pointer;
    font-size: 0.8rem;
    line-height: 1;
    opacity: 0;
    transition: opacity 0.12s ease;
  }
  .row:hover .copy-val {
    opacity: 1;
  }
  .children {
    display: block;
  }
</style>