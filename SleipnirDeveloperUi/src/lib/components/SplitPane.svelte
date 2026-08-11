<script lang="ts">
  interface Props {
    initialLeftWidth?: number;
    initialRightWidth?: number;
    minLeft?: number;
    minRight?: number;
    storageKey?: string;
  }

  let {
    initialLeftWidth = 280,
    initialRightWidth = 360,
    minLeft = 200,
    minRight = 260,
    storageKey = '',
  }: Props = $props();

  let leftWidth = $state(initialLeftWidth);
  let rightWidth = $state(initialRightWidth);
  let dragging = $state(false);
  let containerEl = $state<HTMLDivElement | null>(null);

  // Restore saved sizes
  $effect(() => {
    if (storageKey) {
      try {
        const saved = localStorage.getItem(`sleipnir-split-${storageKey}`);
        if (saved) {
          const [l, r] = JSON.parse(saved);
          if (typeof l === 'number') leftWidth = l;
          if (typeof r === 'number') rightWidth = r;
        }
      } catch {
        /* ignore */
      }
    }
  });

  function saveSizes() {
    if (storageKey) {
      try {
        localStorage.setItem(`sleipnir-split-${storageKey}`, JSON.stringify([leftWidth, rightWidth]));
      } catch {
        /* ignore */
      }
    }
  }

  function onSplitterDown(e: MouseEvent) {
    e.preventDefault();
    dragging = true;
    const startX = e.clientX;
    const startLeft = leftWidth;
    const startRight = rightWidth;

    function onMove(ev: MouseEvent) {
      const dx = ev.clientX - startX;
      const newLeft = Math.max(minLeft, startLeft + dx);
      const newRight = Math.max(minRight, startRight - dx);
      leftWidth = newLeft;
      rightWidth = newRight;
    }

    function onUp() {
      dragging = false;
      saveSizes();
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    }

    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }
</script>

<div class="split-pane" bind:this={containerEl} class:is-dragging={dragging}>
  <div class="pane pane-left" style="width: {leftWidth}px">
    {@render children[0]?.()}
  </div>

  <div
    class="splitter"
    onmousedown={onSplitterDown}
    role="separator"
    tabindex="-1"
  ></div>

  <div class="pane pane-center">
    {@render children[1]?.()}
  </div>

  <div
    class="splitter"
    onmousedown={onSplitterDown}
    role="separator"
    tabindex="-1"
  ></div>

  <div class="pane pane-right" style="width: {rightWidth}px">
    {@render children[2]?.()}
  </div>
</div>

{#if children.length > 3}
  <div class="bottom-panel">
    {@render children[3]?.()}
  </div>
{/if}

<style>
  .split-pane {
    display: flex;
    flex: 1;
    min-height: 0;
    overflow: hidden;
    gap: 0;
  }
  .split-pane.is-dragging {
    cursor: col-resize;
    user-select: none;
  }
  .pane {
    display: flex;
    flex-direction: column;
    min-height: 0;
    overflow: hidden;
    background: var(--bg-elevated);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 10px;
    flex-shrink: 0;
  }
  .pane-center {
    flex: 1;
    min-width: 320px;
    margin: 0 4px;
  }
  .pane-left {
    margin-right: 0;
  }
  .pane-right {
    margin-left: 0;
  }
  .splitter {
    width: 6px;
    cursor: col-resize;
    background: transparent;
    flex-shrink: 0;
    transition: background 0.15s ease;
    position: relative;
    z-index: 10;
  }
  .splitter:hover,
  .is-dragging .splitter {
    background: var(--accent-secondary);
    opacity: 0.4;
  }
  .bottom-panel {
    border-top: 1px solid var(--border);
    background: var(--bg-elevated);
    flex-shrink: 0;
  }
</style>
