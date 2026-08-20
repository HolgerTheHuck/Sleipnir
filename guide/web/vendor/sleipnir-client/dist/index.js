// Sleipnir JavaScript/TypeScript Client — öffentliche API.
// Siehe PROTOCOL.md für das Wire-Format und clients/ts/README.md für Nutzung.
export * from "./types.js";
export { SleipnirError, CancelledError, isCancelled } from "./errors.js";
export { SleipnirCall } from "./fluent.js";
export { SleipnirRestClient, } from "./rest.js";
export { SleipnirWebSocketClient, } from "./websocket.js";
export { SleipnirSseClient, } from "./sse.js";
export { SleipnirSignalrClient, } from "./signalr.js";
export { buildParams, buildSingle, buildMulti, toBase64, fromBase64 } from "./request.js";
export { SleipnirTransportRouter, SleipnirTransportNotBundledError, } from "./transport-router.js";
import { SleipnirRestClient } from "./rest.js";
import { SleipnirWebSocketClient, } from "./websocket.js";
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
export function createClient(baseUrl, options = {}) {
    const { bearer, callTimeout, rest, ws } = options;
    const restClient = new SleipnirRestClient(baseUrl, { ...rest, bearer, callTimeout });
    const wsClient = new SleipnirWebSocketClient(baseUrl, { ...ws, bearer, callTimeout });
    return {
        rest: restClient,
        ws: wsClient,
        setBearer: (b) => {
            restClient.setBearer(b);
            wsClient.setBearer(b);
        },
    };
}
//# sourceMappingURL=index.js.map