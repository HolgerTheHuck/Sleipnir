# Chapter 4 — The Svelte Endkunden-Portal, generated TS client

> **Goal:** a Svelte 5 customer portal that renders a live board of market quotes, calling
> the API over the **unified transport's `auto` mode** — WebSocket probed first, REST + SSE
> as the transparent fallback. A generated **TypeScript** client, full type safety, a Vite
> dev server. This is tier 3 of the 3-tier app.

Chapter 3's HTML page was deliberately minimal: no build, REST only, one file. The portal is
the realistic customer-facing tier — a component framework, a build step, types, and the
**whole transport menu** bundled so `auto` can choose.

```
guide/portal/
  package.json            svelte + vite + sleipnir-client + sleipnir-codegen (devDep)
  vite.config.ts          proxy: every Sleipnir path → Story.Api on 5010
  svelte.config.js        svelte-check reads this (without it, typecheck false-errors)
  tsconfig.json           @tsconfig/svelte, bundler resolution, strict
  index.html              <div id="app"> + /src/main.ts
  src/
    main.ts               Svelte 5 mount(App, …)
    app.css               board + card styles, .up/.down/.flat trend colors
    lib/api.ts            facade: one generated SleipnirClient, same-origin base URL
    api/                  generated TS client (committed; regenerate with `npm run gen`)
    App.svelte            the quote board
```

## Scaffold

```bash
cd guide/portal
npm install      # sleipnir-client + sleipnir-codegen + svelte + vite
npm run gen      # generate src/api/* from the committed contract
npm run dev      # → http://localhost:5173
```

`npm run gen` runs:

```bash
sleipnir-gen --lang ts \
  --discovery ../server/contract.sleipnir.json \
  --out src \
  --base-url https://localhost:5010 \
  --transport all
```

`--transport all` bundles **every** backend (REST, WebSocket, SSE, SignalR) into one client.
That is what makes `auto` meaningful: the router can probe WebSocket and fall back to
REST + SSE at runtime, without regenerating. (`--transport rest`, as in chapter 3, would
bundle REST + SSE only — `auto` would have nothing to probe *up* to.) The `--base-url` is
baked into the generated client as a *default*; the portal overrides it at construction
(see the facade below).

## The generated TS client

```ts
// src/api/client.ts  (generated)
export class SleipnirClient {
  readonly market: MarketClient;
  constructor(baseUrl: string, options: SleipnirClientOptions = {}) { … }
  negotiate(): Promise<void>                  // resolve the `auto` profile
  useTransport(t): Promise<void>             // switch at runtime
  get activeTransport(): … | null             // what `auto` settled on
  async call<T>(call): Promise<TypedResponse<T>>   // data narrowed to T
  async batch<A>(b): Promise<SleipnirResponse[]>  // typed batch (Serial)
  setBearer(b): void                          // chapter 7
  dispose(): void
}

// src/api/controllers.ts  (generated)
export class MarketClient {
  getQuote(symbol: string): TypedCall<Quote | null, QuotePaths> { … }
}
```

**The TS/JS codegen difference worth knowing.** In the JS client (chapter 3) the method is
`async` — it returns a `Promise` wrapping the `Call` builder, so you `await` it before
`client.call(...)`:

```js
const call = await client.market.getQuote("BTC");   // JS: async builder
const res  = await client.call(call);
```

The **TS** client returns the `TypedCall` **synchronously** — no `await` on the builder — so
the call site is one expression:

```ts
const res = await client.call(client.market.getQuote("BTC"));   // TS: sync builder
```

That is the only shape difference; the wire behaviour is identical.

## The facade — same-origin via the Vite proxy

The generated REST client **requires** a non-empty absolute `baseUrl` (it throws on `""`).
But the portal is served by Vite same-origin, and we want the browser to talk *only* to Vite
(plain `http`, no self-signed dev cert to trust, no CORS preflight). The trick: point the
client at `window.location.origin` and let the Vite proxy do the forwarding.

```ts
// src/lib/api.ts
import { SleipnirClient } from "../api/index.js";

export const client = new SleipnirClient(window.location.origin);
export const SEED_SYMBOLS = ["BTC", "ETH", "SOL", "DOGE"] as const;
```

```ts
// vite.config.ts  (the relevant half)
server: {
  port: 5173,
  proxy: {
    '/api/sleipnir': { target: 'https://localhost:5010', changeOrigin: true, secure: false },
    '/events':       { target: 'https://localhost:5010', changeOrigin: true, secure: false },
    '/sleipnirws':   { target: 'https://localhost:5010', changeOrigin: true, secure: false, ws: true },
    '/sleipnirhub':  { target: 'https://localhost:5010', changeOrigin: true, secure: false, ws: true },
  }
}
```

The browser hits `http://localhost:5173/api/sleipnir/json`; Vite forwards it to
`https://localhost:5010`. The same proxy carries the WebSocket upgrade (`/sleipnirws`,
`ws: true`) and the SSE stream (`/events`). One origin, every transport — and `auto` can
probe the proxied WebSocket and fall back to the proxied REST + SSE without the browser
ever knowing the API is on a different port.

## The quote board

`App.svelte` is Svelte 5 runes — `$state` for reactive fields, `onMount` to negotiate the
transport and seed the board, `Promise.all` to fan out one `GetQuote` per symbol.

```svelte
<script lang="ts">
  import { onMount } from "svelte";
  import { client, SEED_SYMBOLS } from "./lib/api.js";
  import type { Quote } from "./api/types.js";

  type Card = { symbol: string; quote: Quote | null; loading: boolean; error: string | null };

  let cards: Card[] = $state([]);
  let newSymbol = $state("");
  let transportLabel = $state("auto (probing…)");

  // Negotiate up front so the badge shows what the router settled on. Negotiation is
  // optional — the first call would trigger it lazily — but doing it here gives us the badge.
  onMount(async () => {
    try {
      await client.negotiate();
      transportLabel = client.activeTransport ?? "auto";
    } catch {
      transportLabel = "auto (WS probe failed → REST+SSE)";
    }
    await refreshAll();
  });

  async function fetchOne(symbol: string): Promise<Card> {
    const card: Card = { symbol, quote: null, loading: true, error: null };
    try {
      const res = await client.call(client.market.getQuote(symbol));
      // getQuote returns null for an unknown symbol — surface it as a friendly error.
      if (res.code === 200 && res.data === null) card.error = `unknown symbol "${symbol}"`;
      else if (res.code === 200) card.quote = res.data;
      else card.error = res.error?.message ?? `HTTP ${res.code}`;
    } catch (e) {
      card.error = e instanceof Error ? e.message : String(e);
    } finally {
      card.loading = false;
    }
    return card;
  }

  async function refreshAll() {
    cards = SEED_SYMBOLS.map((s) => ({ symbol: s, quote: null, loading: true, error: null }));
    cards = await Promise.all(cards.map((c) => fetchOne(c.symbol)));
  }
  // …addSymbol(), trend(), formatPrice() helpers, plus the markup below
</script>
```

The markup renders a card per symbol — price (green/red/grey by `change`), the change
delta, and the quote timestamp — plus an input to add a symbol and a **Refresh all** button.
A badge at the top shows `transport: <strong>{transportLabel}</strong>` so you can *see*
`auto` settle on `ws` or fall back to `rest`/`sse`.

> **Svelte 5 event note:** the form uses the new `onsubmit={(e) => { e.preventDefault(); … }}`
> syntax. Svelte 5 does **not** allow mixing the old `on:submit|preventDefault` with the new
> `onclick`/`onsubmit` syntax in the same component, and the new syntax dropped the
> `|preventDefault` modifier — you call `e.preventDefault()` yourself. (The autofixer flags
> this if you get it wrong.)

## The `svelte.config.js` gotcha

`svelte-check` (which `npm run typecheck` runs) loads the Svelte compiler config. With only
a `vite.config.ts`, svelte-check can false-error:

```
ERROR "src\App.svelte" 1:1 "Error in vite.config —
No Svelte configuration found in vite config. Is @sveltejs/vite-plugin-svelte configured?"
```

This happens because the `file:../../clients/ts` / `file:../../clients/codegen` dependencies
nest their own (vite 5, via vitest) inside `node_modules`, which can confuse svelte-check's
vite-config loader. The fix is the standard one every Svelte + Vite project ships:

```js
// svelte.config.js
import { vitePreprocess } from "@sveltejs/vite-plugin-svelte";
export default { preprocess: vitePreprocess() };
```

With that file present, `npm run typecheck` is green (0 errors, 0 warnings), and
`npm run build` produces a ~83 KB bundle.

## `auto`, negotiation, and the fallback

`new SleipnirClient(window.location.origin)` starts in the `auto` profile — **no backend is
contacted yet**. The first `call()` (or an explicit `client.negotiate()`) probes the
WebSocket handshake against `/sleipnirws`; if it succeeds within `probeTimeout` (default
1500 ms), `auto` resolves to `ws` and calls go over the persistent socket. If the probe
fails — the proxy blocks the upgrade, the cert is wrong, the server is down — `auto`
resolves to `rest`+`sse` and calls go over plain HTTP. The badge on the page reflects the
outcome. You can force a profile at any time:

```ts
await client.useTransport("ws");      // throw if the probe had failed
await client.useTransport("rest");    // escape hatch — always bundled with `all`
```

This is the same "best friends" story from chapter 3, but now the framework chooses the
transport for you at runtime — and the *same* generated client, with no regen, carries
both paths.

## Try it

```bash
# terminal 1 — the API
dotnet run --project guide/server

# terminal 2 — the portal
cd guide/portal && npm run dev
```

Open `http://localhost:5173/`. The board loads BTC, ETH, SOL, DOGE; the badge shows
`transport: ws` (the proxied upgrade succeeds) or `rest` (if something blocked it). Type
`ADA` and click **Add** → a new card with a friendly error (`unknown symbol "ADA"`). Type
`BTC` again → no duplicate. Click **Refresh all** → fresh timestamps.

> **Verify without a browser:** the proxy is just HTTP forwarding, so the same call works
> against the dev server directly:
> ```bash
> curl -s -X POST http://localhost:5173/api/sleipnir/json \
>   -H "Content-Type: application/json" \
>   -d '{"controller":"Market","method":"GetQuote","params":[{"parameterName":"symbol","data":"BTC"}]}'
> # → {"code":200,"data":{"symbol":"BTC","price":60000,"change":-1,"time":"…"}, …}
> ```

## Where this sits in the 3-tier app

The portal is the **customer** tier. So far it only reads public market data — no login, no
holdings. Chapter 5 adds batching (one roundtrip for the whole board instead of four parallel
calls); chapter 7 adds a customer bearer and a `Portfolio` view; chapter 8 wires the live
BTC feed into a sparkline. The Blazor admin (chapter 2) is the **internal** tier — same
contract, same API, different bearer.

---

**Next:** [Chapter 5 — Batching](05-batching.md). Replace the portal's four parallel
`GetQuote` calls and the admin's loop with one `SleipnirMultiRequest` — `Parallel` vs
`Serial` `ExecutionMode`, one roundtrip, N quotes.