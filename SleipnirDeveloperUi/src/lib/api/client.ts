// Dünne Fassade über dem offiziellen Sleipnir TS-Client (sleipnir-client).
// Die DevUI dogfooded hier ihren eigenen Client statt einen handgestrickten
// fetch-Wrapper zu pflegen. Signaturen bleiben identisch, damit Konsumenten
// (EditorPane, discovery.svelte.ts, App.svelte) unverändert bleiben.

import { SleipnirRestClient } from 'sleipnir-client';
import type { DiscoveryInfo, SleipnirMultiRequest, SleipnirRequest, SleipnirResponse } from 'sleipnir-client';

// Verbindung ist modul-lokal mutierbar. `baseUrl "/"` liefert relative Same-Origin-
// Pfade ("/api/sleipnir/..."), sodass die DevUI hinter jedem Proxy/Port läuft, ohne
// window.location zu bemühen — der Standalone-Build überschreibt baseUrl später
// mit "" (User muss Ziel eingeben). `client` ist statelos (nur Konfiguration);
// `rebuild()` instanziiert ihn neu, wenn sich baseUrl/apiPath/bearer ändern. Die
// drei Fassaden-Funktionen schließen über die Modul-Variable und sehen somit
// immer den aktuellen Client.
let baseUrl = '/';
let apiPath = 'api/sleipnir';
let bearer: string | undefined;
let client = new SleipnirRestClient(baseUrl, { apiPath, bearer });

/** Client neu instanziieren, nachdem baseUrl/apiPath/bearer geändert wurden. */
function rebuild(): void {
  client = new SleipnirRestClient(baseUrl, { apiPath, bearer: bearer || undefined });
}

/**
 * Setzt den Bearer-Token für alle nachfolgenden Aufrufe (Discovery, Single,
 * Batch). Leerstring/undefined → kein Authorization-Header (Status quo).
 * Wird vom Auth-State (auth.svelte.ts) gerufen; einweg-Abhängigkeit (kein Zyklus,
 * client.ts importiert keinen State).
 */
export function setBearer(token: string): void {
  bearer = token || undefined;
  rebuild();
}

/**
 * Setzt die Ziel-Verbindung (baseUrl + apiPath) für alle nachfolgenden Aufrufe.
 * Ermöglicht es der (Standalone-)DevUI, auf einen beliebigen Sleipnir-Server zu
 * zeigen statt nur auf Same-Origin `/api/sleipnir`. Wird vom Endpoint-State
 * (endpoint.svelte.ts) gerufen. `url=""` → Client baut relative URLs (kein Host).
 */
export function setEndpoint(url: string, path: string): void {
  baseUrl = url || '/';
  apiPath = path || 'api/sleipnir';
  rebuild();
}

export async function fetchDiscovery(): Promise<DiscoveryInfo> {
  return client.discover();
}

export async function executeRequest(request: SleipnirRequest): Promise<SleipnirResponse> {
  return client.call(request);
}

export async function executeBatch(request: SleipnirMultiRequest): Promise<SleipnirResponse[]> {
  return client.callBatch(request.requests, request.mode);
}

// ─── Observability ───────────────────────────────────────────────────────────
// Roher fetch (nicht über SleipnirRestClient, der keine /observability-Methode
// hat) — spiegelt die Verbindung (baseUrl/apiPath/bearer) aus dem Modul-Zustand.
// Der Endpoint ist opt-in (SleipnirOptions.EnableObservability) und wie /discovery
// RequireAuth-gated; ein 401/non-2xx wird zum Error. Die DevUI dogfood-et hier
// bewusst einen dünnen fetch, da /observability ein Framework-Endpoint (kein RPC)
// ist.

export interface ObservabilitySnapshot {
  transports: { rest: boolean; webSocket: boolean; signalR: boolean; sse: boolean };
  activeConnections: number;
  activeSubscriptions: number;
  eventDroppedTotal: number;
  callCount: number;
  errorCount: number;
  batchCount: number;
  uptimeMs: number;
}

export async function fetchObservability(): Promise<ObservabilitySnapshot> {
  const base = baseUrl.endsWith('/') ? baseUrl : `${baseUrl}/`;
  const url = `${base}${apiPath}/observability`;
  const headers: Record<string, string> = {};
  if (bearer) headers['Authorization'] = `Bearer ${bearer}`;
  const res = await fetch(url, { headers });
  if (!res.ok) {
    throw new Error(`Observability fetch failed: ${res.status} ${res.statusText}`);
  }
  return (await res.json()) as ObservabilitySnapshot;
}