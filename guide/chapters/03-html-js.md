# Chapter 3 — A plain HTML/JS page, generated client, zero build

> **Goal:** call `Market.GetQuote` from a single `index.html` — no bundler, no
> TypeScript, no `npm run build`. A generated JS client, REST + SSE only, loaded
> through an **import map**. This is "Sleipnir & REST — best friends" made concrete.

Tier 3's Svelte portal (chapter 4) has a build step. This chapter is the thinnest
possible client: one HTML file, a generated JS stub, a vendored runtime, and a browser.
It proves how little machinery Sleipnir needs over plain HTTP — `curl` with types.

```
guide/web/
  package.json          one dev dep: sleipnir-codegen (for the `gen` script only)
  index.html            the page — an import map + a <script type="module">
  api/                  generated JS client (committed; regenerate with `npm run gen`)
  vendor/sleipnir-client/dist/   the runtime, vendored (committed)
```

## The gap this chapter fills

The generated JS client imports the bare specifier `sleipnir-client`:

```js
// api/client.js  (generated)
import { SleipnirCall, SleipnirTransportRouter } from "sleipnir-client";
```

But `sleipnir-client` ships **no bundled/standalone browser build** — it is an npm package
of ES modules (`dist/index.js` re-exporting `./rest.js`, `./sse.js`, …). A bundler
(Vite, esbuild) resolves `"sleipnir-client"` from `node_modules` for you. A plain HTML
page has no bundler, so it has no `node_modules` in the browser. **The import map is the
answer:** map the bare specifier to a vendored copy of the runtime `dist`, and the
browser's native module resolver does the rest.

```html
<!-- index.html -->
<script type="importmap">
{
  "imports": {
    "sleipnir-client": "./vendor/sleipnir-client/dist/index.js"
  }
}
</script>
```

The vendored `dist/` is a straight copy of `clients/ts/dist` (44 files). The runtime is
isomorphic: Node-isms are all guarded (`typeof globalThis.WebSocket`, `typeof
globalThis.Buffer`), and the optional `@microsoft/signalr` peer is loaded by a **lazy,
non-literal dynamic `import()`** that only fires if you actually use the SignalR
transport. So evaluating the runtime in a browser never crashes — and with
`--transport rest` the page never touches WebSocket or SignalR anyway.

## Generate the client

`guide/web/package.json` wires the generator as a `file:` dependency and a one-line
script:

```json
{
  "scripts": {
    "gen": "sleipnir-gen --lang js --discovery ../server/contract.sleipnir.json --out . --base-url https://localhost:5010 --transport rest"
  },
  "dependencies": {
    "sleipnir-client": "file:../../clients/ts",
    "sleipnir-codegen": "file:../../clients/codegen"
  }
}
```

```bash
cd guide/web
npm install          # one-time: resolves the file: deps (sleipnir-codegen + sleipnir-client)
npm run gen         # writes ./api/{client,controllers,index,types}.js
```

`--discovery` takes a URL *or* a file. This chapter uses the server's **committed
`contract.sleipnir.json`** — the same single source of truth the Blazor admin links — so
regeneration is reproducible without a running server. Point it at
`https://localhost:5010/api/sleipnir/discovery` to generate from a live API instead.

`--transport rest` makes the generated client bundle REST + SSE only (capability `"rest"`,
no WebSocket) — the proxy-safe, browser-auth-friendly path. The committed `api/` and
`vendor/` mean a cloner never runs `npm` unless they change the contract.

## The page

```html
<script type="module">
  import { SleipnirClient } from "./api/index.js";

  // capability "rest" → "auto" goes straight to REST for calls, SSE for events.
  const client = new SleipnirClient("https://localhost:5010");

  btn.addEventListener("click", async () => {
    // The generated JS methods are async and return a Call builder — await it, then
    // hand it to client.call(...) to execute over REST.
    const call = await client.market.getQuote(symbol);
    const res = await client.call(call);
    if (res.code === 200 && res.data) {
      // res.data is the Quote: { symbol, price, change, time }
    }
  });
</script>
```

Two steps stand out:

- **The import map** precedes the module script and maps `"sleipnir-client"` to the
  vendored dist. Without it, the browser cannot resolve the bare import.
- **`await client.market.getQuote(symbol)` before `client.call(call)`.** The generated
  *JS* methods are `async` (they return a `Call` builder wrapped in a Promise, unlike the
  TS emitter where methods return a `Call` synchronously); await the builder, then
  execute it. (`res.data` is the `Quote`; `res.code` is the envelope status.)

## Serving the page

ES modules cannot `fetch` their own dependencies from a `file://` URL (browser CORS), so
serve `guide/web/` from any static server. Two zero-install options:

```bash
# from inside guide/web/:
python -m http.server 5012        # → http://localhost:5012/index.html
# or
npx serve .                        # → prints a localhost URL
```

Open the printed URL. The page calls `https://localhost:5010` cross-origin — the API's
dev CORS is open (`AllowAnyOrigin`), and `http` → `https` is an allowed (secure-upgrade)
direction, so the call goes through.

## Why REST here, on purpose

This chapter *chooses* REST + SSE. The browser cannot set an `Authorization` header on a
WebSocket upgrade (you'll feel that limit in chapter 7), and corporate proxies often
break opaque WebSocket traffic. REST + SSE has neither problem — and `curl` can still hit
the same endpoint you click. The Svelte portal (next chapter) uses the unified transport's
`auto` mode and falls back to this same REST + SSE path when WebSocket is unavailable; this
chapter is that fallback, on its own.

## Try it

```bash
# terminal 1 — the API
dotnet run --project guide/server

# terminal 2 — serve the page
cd guide/web && python -m http.server 5012
```

Open `http://localhost:5012/`, type `BTC`, click **Get quote**. You should see the price,
change, and time. Try `ETH`, `SOL`, and `NOPE` (→ "No market for symbol 'NOPE'").

> **Verify without a browser:** the generated client runs in Node too — `node -e` an
> import of `./api/index.js` against a running API (set
> `NODE_TLS_REJECT_UNAUTHORIZED=0` for the self-signed dev cert, which the browser
> avoids once you've run `dotnet dev-certs https --trust`).

---

**Next:** [Chapter 4 — the Svelte Endkunden-Portal with a generated TS client](04-svelte-portal.md).
Same quote, but over the unified transport's `auto` mode — WebSocket probed first, this
chapter's REST + SSE as the transparent fallback.