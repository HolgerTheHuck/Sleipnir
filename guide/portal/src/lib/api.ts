// Thin facade over the generated client. The portal is served same-origin by the Vite
// dev server, which proxies every Sleipnir path to Story.Api on 5010 (see vite.config.ts).
// We therefore point the client at window.location.origin: the browser only ever talks
// to the Vite dev server (plain http, no self-signed dev cert to trust), and the proxy
// takes care of CORS and TLS. The unified transport's `auto` profile (the default) then
// probes WebSocket (/sleipnirws, proxied) and falls back to REST+SSE through the same proxy.
import { SleipnirClient } from "../api/index.js";

export const client = new SleipnirClient(window.location.origin);

// The seed symbols the Market controller knows about — see Story.Api MarketController.
export const SEED_SYMBOLS = ["BTC", "ETH", "SOL", "DOGE"] as const;