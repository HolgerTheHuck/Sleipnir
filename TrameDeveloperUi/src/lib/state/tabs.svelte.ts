import type { ControllerMeta, MethodMeta, ParameterMeta, TrameParameter, TypeRef } from 'trame-client';
import { formatJson } from '../utils/json';
import { defaultValueForParam, serializeValueByRef } from '../utils/params';
import { discoveryState } from './discovery.svelte.ts';

export type TabType = 'request' | 'codegen' | 'dependency';

// --- Dependency-Builder-Modell ----------------------------------------------
// Ein DepStep entspricht einem TrameRequest innerhalb eines Serial-Batches mit
// @alias-Abhängigkeitskettung. exposes → dependencyMapping; alias-params → @alias
// in params (native String-Werte mit @-Präfix). Die Struktur ist rein serialisierbar
// (persistiert mit dem Tab).

export interface DepExpose {
  /** Alias-Name (ohne @), den Folgeschritte als @alias referenzieren. */
  alias: string;
  /** Ergebnisrelativer JsonPath — $ = ganzes Result, $.Id, $[0].Id. */
  jsonPath: string;
}

export interface DepParam {
  /** Echter Methoden-Parametername (Server bindet danach). */
  parameterName: string;
  /** Parameter-TypeRef (Snapshot aus Discovery — für die Render-Heuristik komplex vs. skalar). */
  parameterType: TypeRef;
  /** true → Wert ist eine @alias-Referenz auf einen früheren Schritt. */
  useAlias: boolean;
  /** Bei useAlias: Ziel-Alias (ohne @). */
  aliasRef?: string;
  /** Bei !useAlias: roher literal-Wert (String/JSON). */
  literalValue?: string;
}

export interface DepStep {
  /** Schritt-Id — zugleich TrameRequest.id. */
  id: string;
  controller: string;
  method: string;
  params: DepParam[];
  exposes: DepExpose[];
}

export interface Tab {
  id: string;
  type: TabType;
  title: string;
  controller: ControllerMeta | null;
  method: MethodMeta | null;
  params: (ParameterMeta & { value: unknown })[];
  requestText: string;
  responseText: string;
  status: string;
  respIdText: string;
  duration: string;
  log: string;
  /** Schritte für den Dependency-Builder (nur bei type='dependency'). */
  steps?: DepStep[];
}

const STORAGE_KEY = 'trame-tabs';

let counter = 0;

function generateId(): string {
  return `${Date.now()}-${Math.random().toString(16).slice(2, 6)}-${counter++}`;
}

/** Erzeugt einen leeren Dependency-Builder-Schritt mit vorgegebener Id. */
function makeDefaultStep(id: string): DepStep {
  return { id, controller: '', method: '', params: [], exposes: [] };
}

// --- Persistenz -----------------------------------------------------------
// `Tab` ist rein serialisierbar (controller/method sind Discovery-Snapshots),
// daher kann der komplette Tab-Zustand als JSON in localStorage liegen und beim
// Start wiederhergestellt werden. try/catch wie bei theme/history/auth.

interface StoredTabs {
  tabs: Tab[];
  activeTabId: string | null;
}

function loadFromStorage(): StoredTabs {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<StoredTabs>;
      if (Array.isArray(parsed.tabs)) {
        return { tabs: parsed.tabs as Tab[], activeTabId: parsed.activeTabId ?? null };
      }
    }
  } catch {
    /* ignore */
  }
  return { tabs: [], activeTabId: null };
}

function saveToStorage(tabs: Tab[], activeTabId: string | null): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ tabs, activeTabId }));
  } catch {
    /* ignore */
  }
}

function buildDefaultPayload(params: (ParameterMeta & { value: unknown })[]): TrameParameter[] {
  return params.map((p) => ({
    parameterName: p.parameterName,
    // `data` ist der NATIVE JSON-Wert (kein JSON-String mehr): Objekt als Record,
    // Skalare als native JS-Werte (number/boolean/string). Serialisierung folgt
    // dem Parameter-TypeRef (docs/discovery-schema.md), nicht .NET-Namen-Strings.
    data: serializeValueByRef(p.value, p.parameterType, discoveryState.data),
  }));
}

class TabState {
  // Initial aus localStorage; beim allerersten Start (kein Storage) leer →
  // App.svelte onMount legt dann genau einen Welcome-Tab an.
  tabs = $state<Tab[]>(loadFromStorage().tabs);
  activeTabId = $state<string | null>(loadFromStorage().activeTabId);

  get activeTab(): Tab | undefined {
    return this.tabs.find((t) => t.id === this.activeTabId);
  }

  /** Persistiert den aktuellen Tab-Zustand. Aufruf aus jedem Mutator. */
  persist(): void {
    saveToStorage(this.tabs, this.activeTabId);
  }

  createTab(partial: Partial<Tab> = {}): Tab {
    const tab: Tab = {
      type: 'request',
      id: generateId(),
      title: 'New request',
      controller: null,
      method: null,
      params: [],
      requestText: '[]',
      responseText: '{}',
      status: '-',
      respIdText: '-',
      duration: '-- ms',
      log: '',
      ...partial,
    };
    this.tabs.push(tab);
    this.activeTabId = tab.id;
    this.persist();
    return tab;
  }

  createCodegenTab(): Tab {
    // Reuse existing codegen tab if open
    const existing = this.tabs.find((t) => t.type === 'codegen');
    if (existing) {
      this.activeTabId = existing.id;
      this.persist();
      return existing;
    }
    const tab: Tab = {
      type: 'codegen',
      id: generateId(),
      title: 'Codegen',
      controller: null,
      method: null,
      params: [],
      requestText: '',
      responseText: '',
      status: '',
      respIdText: '',
      duration: '',
      log: '',
    };
    this.tabs.push(tab);
    this.activeTabId = tab.id;
    this.persist();
    return tab;
  }

  createDependencyTab(): Tab {
    // Existierenden Dependency-Tab wiederverwenden (wie Codegen).
    const existing = this.tabs.find((t) => t.type === 'dependency');
    if (existing) {
      this.activeTabId = existing.id;
      this.persist();
      return existing;
    }
    const tab: Tab = {
      type: 'dependency',
      id: generateId(),
      title: 'Dependency Builder',
      controller: null,
      method: null,
      params: [],
      requestText: '',
      responseText: '{}',
      status: '-',
      respIdText: '-',
      duration: '-- ms',
      log: '',
      steps: [makeDefaultStep('step1')],
    };
    this.tabs.push(tab);
    this.activeTabId = tab.id;
    this.persist();
    return tab;
  }

  closeTab(id: string) {
    if (this.tabs.length <= 1) return;
    const idx = this.tabs.findIndex((t) => t.id === id);
    if (idx === -1) return;
    this.tabs.splice(idx, 1);
    if (this.activeTabId === id) {
      const fallback = this.tabs[Math.max(0, idx - 1)];
      this.activeTabId = fallback.id;
    }
    this.persist();
  }

  switchTab(id: string) {
    this.activeTabId = id;
    this.persist();
  }

  openMethodTab(controller: ControllerMeta, method: MethodMeta) {
    const existing = this.tabs.find(
      (t) => t.controller?.name === controller.name && t.method?.methodName === method.methodName
    );
    if (existing) {
      this.activeTabId = existing.id;
      this.persist();
      return existing;
    }
    const params = method.parameters.map((p) => ({ ...p, value: defaultValueForParam(p, discoveryState.data) }));
    const payload = formatJson(buildDefaultPayload(params));
    return this.createTab({
      title: `${controller.name}.${method.methodName}`,
      controller,
      method,
      params,
      requestText: payload,
    });
  }

  syncRequestFromParams(tab: Tab) {
    const payload = buildDefaultPayload(tab.params);
    tab.requestText = formatJson(payload);
    this.persist();
  }

  syncParamsFromEditor(tab: Tab, editorText: string) {
    try {
      const arr = JSON.parse(editorText);
      if (Array.isArray(arr)) {
        for (const p of tab.params) {
          const match = arr.find((x: TrameParameter) => x.parameterName === p.parameterName);
          if (match) {
            // data ist jetzt nativ (kein JSON-String mehr) → direkt übernehmen.
            p.value = match.data;
          }
        }
      }
      tab.requestText = editorText;
      this.persist();
    } catch {
      /* ignore parse errors */
    }
  }

  /**
   * Zentrale Stelle für das Schreiben eines Call-Ergebnisses in den Tab. Wird
   * von EditorPane nach einem erfolgreichen Single/Batch-Call gerufen, damit
   * jeder Ergebnis-Write auch persistiert wird (statt direkter Feld-Schreibs).
   */
  applyResult(
    tab: Tab,
    result: { code?: number | null; id?: string | null; data?: unknown; isSuccess?: boolean; error?: { message?: string } | null },
    duration: string,
    responseText: string,
    status: string,
    log = '',
  ): void {
    tab.duration = duration;
    tab.status = status;
    tab.respIdText = result.id ?? '-';
    tab.responseText = responseText;
    tab.log = log;
    this.persist();
  }

  /**
   * Ersetzt den Tab-Zustand durch einen importierten Workspace. Validiert das
   * Array, übernimmt tabs + activeTabId (falls enthalten) und persistiert.
   */
  restoreFromWorkspace(tabs: Tab[], activeTabId: string | null): void {
    if (!Array.isArray(tabs)) return;
    this.tabs = tabs;
    this.activeTabId = activeTabId && tabs.some((t) => t.id === activeTabId) ? activeTabId : (tabs[0]?.id ?? null);
    this.persist();
  }
}

export const tabState = new TabState();