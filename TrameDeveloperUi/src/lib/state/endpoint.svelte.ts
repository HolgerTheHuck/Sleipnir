import { setEndpoint } from '../api/client';

const STORAGE_KEY = 'trame-endpoint';

// Standalone-Build (vite --mode standalone) → DevUI läuft nicht eingebettet,
// sondern von beliebigem Host (z. B. GitHub Pages) und muss auf einen fremden
// Trame-Server zeigen. Default ist dann leer (User muss Ziel eingeben). Eingebettet
// bleibt "/" (Same-Origin) wie bisher — keine Regression.
const standalone = import.meta.env.MODE === 'standalone';
const defaultBaseUrl = standalone ? '' : '/';
const defaultApiPath = 'api/trame';

interface StoredEndpoint {
  baseUrl: string;
  apiPath: string;
}

function loadInitial(): StoredEndpoint {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<StoredEndpoint>;
      return {
        baseUrl: typeof parsed.baseUrl === 'string' ? parsed.baseUrl : defaultBaseUrl,
        apiPath: typeof parsed.apiPath === 'string' ? parsed.apiPath : defaultApiPath,
      };
    }
  } catch {
    /* ignore */
  }
  return { baseUrl: defaultBaseUrl, apiPath: defaultApiPath };
}

function persist(baseUrl: string, apiPath: string): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ baseUrl, apiPath }));
  } catch {
    /* ignore */
  }
}

/**
 * Endpoint-State der DevUI. Spiegelt auth.svelte.ts/theme.svelte.ts (Svelte-5-
 * Runes-Klasse, localStorage, try/catch). Werte werden via setEndpoint in die
 * statelose Fassade (client.ts) durchgereicht — jede Änderung greift sofort auf
 * alle nachfolgenden Calls (Discovery, Single, Batch).
 *
 * `isCustom` meldet, ob die Connection vom eingebetteten Default ("/"/"api/trame")
 * abweicht — für den Aktiv-Indikator am ⚙-Button.
 */
class EndpointState {
  baseUrl = $state<string>(loadInitial().baseUrl);
  apiPath = $state<string>(loadInitial().apiPath);

  constructor() {
    // Fassade beim Start mit der persistierten Connection synken.
    setEndpoint(this.baseUrl, this.apiPath);
  }

  /** True, wenn nicht der eingebettete Same-Origin-Default aktiv ist. */
  get isCustom(): boolean {
    return this.baseUrl !== defaultBaseUrl || this.apiPath !== defaultApiPath;
  }

  /** Setzt Connection, persistiert und synct die Fassade. */
  set(url: string, path: string): void {
    this.baseUrl = url;
    this.apiPath = path || defaultApiPath;
    persist(this.baseUrl, this.apiPath);
    setEndpoint(this.baseUrl, this.apiPath);
  }
}

export const endpointState = new EndpointState();