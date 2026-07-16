// Workspace-Export/-Import: fasst den kompletten DevUI-Arbeitsstand in einer
// JSON-Datei zusammen, damit man einen fixen Stand (z. B. für einen Trame-
// Server, eine Stand-Alone-Sitzung) aufbewahrt und später wiederherstellt.
//
// Version 2 umfasst neben Connection + Tabs auch Theme, Layout (Split-Größen)
// und History. Version 1 (Connection + Tabs) bleibt importierbar (Rückwärts-
// kompatibilität). Der Bearer-Token wird bewusst NICHT exportiert (Secret-Leak-
// Schutz); er bleibt separat in trame-bearer und greift nach Import weiter.

import type { Tab } from './tabs.svelte';
import { tabState } from './tabs.svelte';
import { endpointState } from './endpoint.svelte';
import { themeState } from './theme.svelte';
import { layoutState } from './layout.svelte';
import { historyState, type HistoryEntry } from './history.svelte';

export interface WorkspaceConnection {
  baseUrl: string;
  apiPath: string;
}

export interface WorkspaceSettings {
  theme: 'dark' | 'light';
}

export interface WorkspaceLayout {
  leftWidth: number;
  rightWidth: number;
  discoveryHeight: number;
}

export interface Workspace {
  version: 2;
  /** ISO-Zeitstempel — nur für menschliche Lesbarkeit, wird beim Import ignoriert. */
  exportedAt: string;
  connection: WorkspaceConnection;
  tabs: Tab[];
  activeTabId: string | null;
  settings?: WorkspaceSettings;
  layout?: WorkspaceLayout;
  /** ≤100 Einträge (historyState deckelt ohnehin). */
  history?: HistoryEntry[];
}

const MAX_HISTORY = 100;

/** Zeitstempel-Suffix YYYY-MM-DD-HHMM für den Download-Dateinamen. */
function timestamp(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}`;
}

function filename(): string {
  return `trame-workspace-${timestamp()}.json`;
}

/** Baut den aktuellen Workspace-Stand (ohne Bearer). */
export function buildWorkspace(): Workspace {
  return {
    version: 2,
    exportedAt: new Date().toISOString(),
    connection: { baseUrl: endpointState.baseUrl, apiPath: endpointState.apiPath },
    tabs: tabState.tabs,
    activeTabId: tabState.activeTabId,
    settings: { theme: themeState.theme },
    layout: {
      leftWidth: layoutState.leftWidth,
      rightWidth: layoutState.rightWidth,
      discoveryHeight: layoutState.discoveryHeight,
    },
    history: historyState.entries.slice(0, MAX_HISTORY),
  };
}

/**
 * Speichert den Workspace. Bevorzugt der native Speichern-Dialog (File System
 * Access API, Chromium: Edge/Chrome), damit der User Ordner UND Dateiname frei
 * wählen kann — nicht nur der Default-Download-Ordner. Der Zeitstempel-Name
 * steht als Vorschlag im Picker. Fallback für Firefox/Safari: klassischer
 * Blob-Download (landet im Default-Download-Ordner). Abbruch durch den User
 * (AbortError) ist still, wirft nicht.
 */
export async function exportWorkspaceFile(): Promise<void> {
  const json = JSON.stringify(buildWorkspace(), null, 2);
  const suggested = filename();

  // Minimal-Typ ohne globale Namenskollision — showSaveFilePicker fehlt in
  // älteren lib.dom.d.ts, deshalb über unknown casten statt interface zu deklarieren.
  const w = window as unknown as {
    showSaveFilePicker?: (opts: {
      suggestedName?: string;
      types?: { description?: string; accept: Record<string, string[]> }[];
    }) => Promise<{
      createWritable: () => Promise<{
        write: (data: string) => Promise<void>;
        close: () => Promise<void>;
      }>;
    }>;
  };

  if (typeof w.showSaveFilePicker === 'function') {
    try {
      const handle = await w.showSaveFilePicker({
        suggestedName: suggested,
        types: [{ description: 'Trame Workspace', accept: { 'application/json': ['.json'] } }],
      });
      const writable = await handle.createWritable();
      await writable.write(json);
      await writable.close();
      return;
    } catch (err) {
      // User hat den Dialog abgebrochen → still, kein Fallback.
      if (err instanceof DOMException && err.name === 'AbortError') return;
      // Sonstiger Fehler (z. B. Berechtigungsabbruch) → Download-Fallback.
    }
  }

  downloadBlob(json);
}

/** Klassischer Blob-Download — Fallback ohne Speichern-Dialog. */
function downloadBlob(json: string): void {
  const blob = new Blob([json], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename();
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

/**
 * Importiert einen Workspace aus JSON-Text: parst, prüft die Version (1 oder 2),
 * setzt die Verbindung (Endpoint-State + Fassade) und stellt die Tabs wieder
 * her. Version 2 ergänzt Theme, Layout und History (live/reaktiv). Bearer bleibt
 * unangetastet. Wirft bei ungültigem Input (Aufrufer fängt).
 */
export function importWorkspaceFromText(json: string): void {
  const parsed = JSON.parse(json) as Partial<Workspace>;
  if (!parsed || typeof parsed !== 'object') {
    throw new Error('Kein gültiges Workspace-JSON.');
  }
  if (parsed.version !== 1 && parsed.version !== 2) {
    throw new Error(`Nicht unterstützte Workspace-Version: ${parsed.version ?? 'fehlt'}.`);
  }
  if (!Array.isArray(parsed.tabs)) {
    throw new Error('Workspace enthält keine Tabs.');
  }
  const conn = parsed.connection ?? { baseUrl: '/', apiPath: 'api/trame' };
  endpointState.set(conn.baseUrl ?? '/', conn.apiPath ?? 'api/trame');
  tabState.restoreFromWorkspace(parsed.tabs as Tab[], parsed.activeTabId ?? null);

  // Version-2-Erweiterungen — jeweils nur vorhanden, wenn das Feld im JSON steht.
  if (parsed.version === 2) {
    if (parsed.settings?.theme) {
      themeState.set(parsed.settings.theme);
    }
    if (parsed.layout) {
      layoutState.replaceAll(parsed.layout);
    }
    if (Array.isArray(parsed.history)) {
      historyState.restoreFromWorkspace(parsed.history);
    }
  }
}