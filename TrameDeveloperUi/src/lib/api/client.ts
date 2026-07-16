// Dünne Fassade über dem offiziellen Trame TS-Client (trame-client).
// Die DevUI dogfooded hier ihren eigenen Client statt einen handgestrickten
// fetch-Wrapper zu pflegen. Signaturen bleiben identisch, damit Konsumenten
// (EditorPane, discovery.svelte.ts, App.svelte) unverändert bleiben.

import { TrameRestClient } from 'trame-client';
import type { DiscoveryInfo, TrameMultiRequest, TrameRequest, TrameResponse } from 'trame-client';

// Verbindung ist modul-lokal mutierbar. `baseUrl "/"` liefert relative Same-Origin-
// Pfade ("/api/trame/..."), sodass die DevUI hinter jedem Proxy/Port läuft, ohne
// window.location zu bemühen — der Standalone-Build überschreibt baseUrl später
// mit "" (User muss Ziel eingeben). `client` ist statelos (nur Konfiguration);
// `rebuild()` instanziiert ihn neu, wenn sich baseUrl/apiPath/bearer ändern. Die
// drei Fassaden-Funktionen schließen über die Modul-Variable und sehen somit
// immer den aktuellen Client.
let baseUrl = '/';
let apiPath = 'api/trame';
let bearer: string | undefined;
let client = new TrameRestClient(baseUrl, { apiPath, bearer });

/** Client neu instanziieren, nachdem baseUrl/apiPath/bearer geändert wurden. */
function rebuild(): void {
  client = new TrameRestClient(baseUrl, { apiPath, bearer: bearer || undefined });
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
 * Ermöglicht es der (Standalone-)DevUI, auf einen beliebigen Trame-Server zu
 * zeigen statt nur auf Same-Origin `/api/trame`. Wird vom Endpoint-State
 * (endpoint.svelte.ts) gerufen. `url=""` → Client baut relative URLs (kein Host).
 */
export function setEndpoint(url: string, path: string): void {
  baseUrl = url || '/';
  apiPath = path || 'api/trame';
  rebuild();
}

export async function fetchDiscovery(): Promise<DiscoveryInfo> {
  return client.discover();
}

export async function executeRequest(request: TrameRequest): Promise<TrameResponse> {
  return client.call(request);
}

export async function executeBatch(request: TrameMultiRequest): Promise<TrameResponse[]> {
  return client.callBatch(request.requests, request.mode);
}