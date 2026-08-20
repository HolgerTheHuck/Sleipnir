// Sleipnir JavaScript/TypeScript Client — öffentliche API.
// Siehe PROTOCOL.md für das Wire-Format und clients/ts/README.md für Nutzung.

export * from "./types.js";
export { SleipnirError, CancelledError, isCancelled } from "./errors.js";
export { SleipnirCall } from "./fluent.js";
export {
  SleipnirRestClient,
  type SleipnirRestClientOptions,
  type CallOptions,
} from "./rest.js";
export {
  SleipnirWebSocketClient,
  type SleipnirWebSocketClientOptions,
  type WsCallOptions,
  type SubscribeOptions,
  type IWebSocket,
  type WsFactory,
  type SubscribeHandlers,
  type SleipnirSubscription,
  type ResumeDecision,
  type SubscriptionResumeContext,
  type ResumePolicy,
} from "./websocket.js";
export {
  SleipnirSseClient,
  type SleipnirSseClientOptions,
  type SseSubscribeOptions,
  type SseFetchLike,
} from "./sse.js";
export {
  SleipnirSignalrClient,
  type SleipnirSignalrClientOptions,
  type SignalrCallOptions,
  type SignalrSubscribeOptions,
  type SignalrHubFactory,
  type SignalrBuildOptions,
  type IHubConnection,
  type IStreamResult,
  type IStreamSubscriber,
} from "./signalr.js";
export { buildParams, buildSingle, buildMulti, toBase64, fromBase64 } from "./request.js";
export {
  SleipnirTransportRouter,
  SleipnirTransportNotBundledError,
  type SleipnirTransport,
  type SleipnirBundleCapability,
  type SleipnirRouterOptions,
  type SleipnirSubscribeOptions,
} from "./transport-router.js";

import { SleipnirRestClient, type SleipnirRestClientOptions } from "./rest.js";
import {
  SleipnirWebSocketClient,
  type SleipnirWebSocketClientOptions,
} from "./websocket.js";
import type { BearerProvider } from "./types.js";

/** Gemeinsame Bearer/Timeout-Optionen für createClient. */
export interface CreateClientOptions {
  bearer?: BearerProvider;
  callTimeout?: number;
  rest?: Omit<SleipnirRestClientOptions, "bearer" | "callTimeout">;
  ws?: Omit<SleipnirWebSocketClientOptions, "bearer" | "callTimeout">;
}

export interface SleipnirClient {
  rest: SleipnirRestClient;
  ws: SleipnirWebSocketClient;
  /** Tauscht den Bearer auf beiden Clients (REST pro Call, WS ab nächstem Connect). */
  setBearer: (bearer: BearerProvider) => void;
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
export function createClient(baseUrl: string, options: CreateClientOptions = {}): SleipnirClient {
  const { bearer, callTimeout, rest, ws } = options;
  const restClient = new SleipnirRestClient(baseUrl, { ...rest, bearer, callTimeout });
  const wsClient = new SleipnirWebSocketClient(baseUrl, { ...ws, bearer, callTimeout });
  return {
    rest: restClient,
    ws: wsClient,
    setBearer: (b: BearerProvider) => {
      restClient.setBearer(b);
      wsClient.setBearer(b);
    },
  };
}