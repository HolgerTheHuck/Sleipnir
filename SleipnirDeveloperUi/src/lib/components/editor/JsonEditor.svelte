<script lang="ts">
  import { formatJson } from '../../utils/json';

  let { value = '', onchange }: { value: string; onchange?: (v: string) => void } = $props();

  function handleInput(e: Event) {
    const newValue = (e.target as HTMLTextAreaElement).value;
    value = newValue;
    onchange?.(newValue);
  }

  function handleFormat() {
    value = formatJson(value);
    onchange?.(value);
  }
</script>

<div class="json-editor">
  <div class="editor-toolbar">
    <span class="field-label">JSON Request</span>
    <button class="ghost small" onclick={handleFormat}>Format</button>
  </div>
  <textarea
    class="code editor-area"
    value={value}
    oninput={handleInput}
    spellcheck="false"
    placeholder="[]"
  ></textarea>
</div>

<style>
  .json-editor {
    display: flex;
    flex-direction: column;
    flex: 1;
    min-height: 0;
  }
  .editor-toolbar {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 4px;
  }
  .editor-area {
    flex: 1;
    min-height: 120px;
    resize: none;
    font-family: var(--font-mono);
    font-size: 0.85rem;
    line-height: 1.5;
    tab-size: 2;
    padding: 10px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--code-bg);
    color: var(--code-text);
  }
</style>
