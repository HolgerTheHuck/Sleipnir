// Trame JavaScript/TypeScript Client — öffentliche API.
// Siehe PROTOCOL.md für das Wire-Format und clients/ts/README.md für Nutzung.

export * from "./types.js";
export { TrameError, CancelledError, isCancelled } from "./errors.js";
export { TrameCall } from "./fluent.js";
export {
  TrameRestClient,
  type TrameRestClientOptions,
  type CallOptions,
} from "./rest.js";
export {
  TrameWebSocketClient,
  type TrameWebSocketClientOptions,
  type WsCallOptions,
  type IWebSocket,
  type WsFactory,
} from "./websocket.js";
export { buildParams, buildSingle, buildMulti, toBase64, fromBase64 } from "./request.js";

import { TrameRestClient, type TrameRestClientOptions } from "./rest.js";
import {
  TrameWebSocketClient,
  type TrameWebSocketClientOptions,
} from "./websocket.js";

/** Gemeinsame Bearer/Timeout-Optionen für createClient. */
export interface CreateClientOptions {
  bearer?: string;
  callTimeout?: number;
  rest?: Omit<TrameRestClientOptions, "bearer" | "callTimeout">;
  ws?: Omit<TrameWebSocketClientOptions, "bearer" | "callTimeout">;
}

export interface TrameClient {
  rest: TrameRestClient;
  ws: TrameWebSocketClient;
}

/**
 * Convenience-Factory: erzeugt ein REST- und ein WebSocket-Client-Paar mit
 * gemeinsamen Bearer/Timeout-Optionen.
 *
 * ```ts
 * const { rest, ws } = createClient("https://localhost:5001", { bearer: token });
 * const customer = await rest.callJson<Customer>("Customer", "GetById", { id: 42 });
 * const wsClient = ws; await wsClient.connect();
 * ```
 */
export function createClient(baseUrl: string, options: CreateClientOptions = {}): TrameClient {
  const { bearer, callTimeout, rest, ws } = options;
  return {
    rest: new TrameRestClient(baseUrl, { ...rest, bearer, callTimeout }),
    ws: new TrameWebSocketClient(baseUrl, { ...ws, bearer, callTimeout }),
  };
}