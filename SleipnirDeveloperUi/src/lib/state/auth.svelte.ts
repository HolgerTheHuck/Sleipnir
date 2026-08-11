import { setBearer } from '../api/client';

const STORAGE_KEY = 'sleipnir-bearer';

function getInitialBearer(): string {
  try {
    return localStorage.getItem(STORAGE_KEY) ?? '';
  } catch {
    /* ignore */
  }
  return '';
}

function persistBearer(token: string): void {
  try {
    if (token) localStorage.setItem(STORAGE_KEY, token);
    else localStorage.removeItem(STORAGE_KEY);
  } catch {
    /* ignore */
  }
}

/**
 * Bearer-Auth-State für die DevUI. Spiegelt das theme.svelte.ts-Pattern
 * (Svelte-5-Runes-Klasse, localStorage, try/catch). Der Token greift auf JEDEN
 * Aufruf (Discovery, Single, Batch), weil die Fassade (client.ts) ihn via
 * setBearer in den statelosen SleipnirRestClient einbaut — nativer Pfad, kein
 * Header-Gefrickel pro Call.
 *
 * Sicherheitshinweis: der Token liegt unverschlüsselt in localStorage (Dev-Tool-
 * Konvention, analog Theme/History). Clear() wischt ihn. Für ein echtes Token-
 * Handling (Rotation/Refresh/Login-Flow) wäre ein eigener Auth-Flow nötig —
 * bewusst nicht in der DevUI (manuelle Eingabe als Dev-Werkzeug).
 */
class AuthState {
  bearer = $state<string>(getInitialBearer());

  constructor() {
    // Fassade beim Start mit dem persistierten Token synken.
    setBearer(this.bearer);
  }

  /** True, wenn ein Token gesetzt ist (für den Aktiv-Indikator am Schlüssel-Icon). */
  get hasToken(): boolean {
    return this.bearer.trim().length > 0;
  }

  /** Setzt den Token, persistiert ihn und synct die Fassade. */
  set(token: string): void {
    const trimmed = token.trim();
    this.bearer = trimmed;
    persistBearer(trimmed);
    setBearer(trimmed);
  }

  /** Leert den Token, wischt localStorage und synct die Fassade (kein Header). */
  clear(): void {
    this.bearer = '';
    persistBearer('');
    setBearer('');
  }
}

export const authState = new AuthState();