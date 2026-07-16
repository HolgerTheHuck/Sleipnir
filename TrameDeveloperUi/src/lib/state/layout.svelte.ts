// Layout-State der DevUI: zentralisiert die Split-Größen (links/center/rechts
// aus App.svelte + die Discovery↔Types-Höhe aus ExplorerPane) als reaktiver
// Single-Source-of-Truth. Alle drei Komponenten plus workspace.ts laufen über
// dieses Singleton — damit ist der komplette UI-Layout-Stand serialisierbar
// und beim Workspace-Import live (ohne Reload) wiederherstellbar.
//
// Spiegelt das theme.svelte.ts/endpoint.svelte.ts-Pattern (Svelte-5-Runes-
// Klasse, localStorage, try/catch). persist() wird bewusst nur am Drag-Ende
// gerufen, nicht pro Mausbewegung.

const STORAGE_KEY = 'trame-layout';
// Legacy-Keys aus der Zeit vor layoutState — als Fallback gelesen, damit
// bestehende Nutzer ihre gezogenen Split-Größen nach dem Build-Update nicht
// verlieren.
const LEGACY_APP_SPLIT = 'trame-split-sizes';
const LEGACY_EXPLORER_VSPLIT = 'trame-explorer-vsplit';

interface StoredLayout {
  leftWidth: number;
  rightWidth: number;
  discoveryHeight: number;
}

const DEFAULTS: StoredLayout = {
  leftWidth: 280,
  rightWidth: 360,
  discoveryHeight: 320,
};

function isNum(v: unknown): v is number {
  return typeof v === 'number' && !Number.isNaN(v);
}

function loadInitial(): StoredLayout {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) {
      const p = JSON.parse(raw) as Partial<StoredLayout>;
      return {
        leftWidth: isNum(p.leftWidth) ? p.leftWidth : DEFAULTS.leftWidth,
        rightWidth: isNum(p.rightWidth) ? p.rightWidth : DEFAULTS.rightWidth,
        discoveryHeight: isNum(p.discoveryHeight) ? p.discoveryHeight : DEFAULTS.discoveryHeight,
      };
    }
  } catch {
    /* ignore */
  }
  // Legacy-Fallback: alte Einzel-Keys übernehmen, falls das neue noch fehlt.
  try {
    const appRaw = localStorage.getItem(LEGACY_APP_SPLIT);
    const legacy = appRaw ? (JSON.parse(appRaw) as [number, number] | unknown) : null;
    const left = Array.isArray(legacy) && isNum(legacy[0]) ? legacy[0] : DEFAULTS.leftWidth;
    const right = Array.isArray(legacy) && isNum(legacy[1]) ? legacy[1] : DEFAULTS.rightWidth;
    const dhRaw = localStorage.getItem(LEGACY_EXPLORER_VSPLIT);
    const dh = dhRaw ? JSON.parse(dhRaw) : null;
    const discoveryHeight = isNum(dh) ? dh : DEFAULTS.discoveryHeight;
    return { leftWidth: left, rightWidth: right, discoveryHeight };
  } catch {
    /* ignore */
  }
  return { ...DEFAULTS };
}

class LayoutState {
  leftWidth = $state<number>(loadInitial().leftWidth);
  rightWidth = $state<number>(loadInitial().rightWidth);
  discoveryHeight = $state<number>(loadInitial().discoveryHeight);

  persist(): void {
    try {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({
          leftWidth: this.leftWidth,
          rightWidth: this.rightWidth,
          discoveryHeight: this.discoveryHeight,
        }),
      );
    } catch {
      /* ignore */
    }
  }

  /** Überschreibt alle vorhandenen Größen aus einem Workspace-Import. */
  replaceAll(sizes: Partial<StoredLayout>): void {
    if (isNum(sizes.leftWidth)) this.leftWidth = sizes.leftWidth;
    if (isNum(sizes.rightWidth)) this.rightWidth = sizes.rightWidth;
    if (isNum(sizes.discoveryHeight)) this.discoveryHeight = sizes.discoveryHeight;
    this.persist();
  }
}

export const layoutState = new LayoutState();