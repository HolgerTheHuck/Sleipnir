<script lang="ts">
  import { onMount } from "svelte";
  import { client, SEED_SYMBOLS } from "./lib/api.js";
  import { Batch } from "./api/index.js";
  import type { PriceTick, Quote, Profile, Holding } from "./api/types.js";
  import type { SleipnirSubscription } from "sleipnir-client";

  type Card = { symbol: string; quote: Quote | null; loading: boolean; error: string | null };

  let cards: Card[] = $state([]);
  let newSymbol = $state("");
  let transportLabel = $state("auto (probing…)");
  // Chapter 5: "batch" sends ONE SleipnirMultiRequest of N GetQuote calls (one roundtrip,
  // composing existing methods); "bulk" calls the single Market.GetQuotes endpoint. Chapter 4
  // did Promise.all of N calls = N roundtrips; both options below are one roundtrip.
  let fetchMode: "batch" | "bulk" = $state("batch");

  // Chapter 6: a dependency CHAIN in one roundtrip. Search(query) is the provider — it
  // exposes $[*] (every element of its string[] result) as the alias "symbols". GetQuotes
  // is the consumer — it takes @symbols as its parameter. The server resolves @symbols in
  // Serial mode, so the two calls fold into one SleipnirMultiRequest and one response array.
  let chainQuery = $state("bit");
  let chainSymbols: string[] = $state([]);
  let chainQuotes: Quote[] = $state([]);
  let chainError: string | null = $state(null);
  let chainLoading = $state(false);

  // Chapter 8: auth. A browser WebSocket CANNOT set the Authorization header, so authed calls
  // must go over REST+SSE. After a successful Login the portal calls setBearer(token) AND
  // useTransport("rest") — the server-side admin (Blazor) keeps `auto` because its C# WS client
  // can set the header; the browser portal cannot, so "REST best friends" for authed calls.
  let loginUser = $state("customer");
  let loginPass = $state("customer");
  let profile: Profile | null = $state(null);
  let loginError: string | null = $state(null);
  let loggingIn = $state(false);
  let holdings: Holding[] = $state([]);
  let holdingsError: string | null = $state(null);
  let loadingHoldings = $state(false);

  // Chapter 9: the live BTC price feed (a server-push [SleipnirEvent]). The portal subscribes to
  // PriceFeed.Ticks("BTC") and draws a rolling sparkline. The feed is anonymous (no
  // [SleipnirAuthorise] — subscribe as anyone); the admin-only StartFeed/StopFeed controls whether
  // anything is produced. Transport is selectable: `auto` (WS), `sse` (REST+SSE — the "REST best
  // friends" path), or `signalr`. The subscription handle carries subscriptionId + lastEventId;
  // "Drop & resume over SSE" demonstrates cross-transport resume against the durable store.
  let feedTransport: "auto" | "rest" | "ws" | "signalr" = $state("auto");
  let priceSeries: number[] = $state([]);
  let feedTicks: PriceTick[] = $state([]);
  let feedSub: SleipnirSubscription | null = $state(null);
  let feedSubId = $state("");
  let feedLastEventId = $state(0);
  let feedStatus = $state<string | null>(null);
  let feedBusy = $state(false);
  let resumeStatus = $state<string | null>(null);

  function authedTransportLabel(): string {
    // After login we pin REST+SSE; reflect that in the badge so the "authed → REST" story is visible.
    return profile ? "rest+sse (authed)" : transportLabel;
  }

  // Negotiate the `auto` profile once on mount so the badge reflects what the router
  // actually settled on (WebSocket, or the REST+SSE fallback). Negotiation is optional —
  // the first call would trigger it lazily — but doing it up front gives us the badge.
  onMount(async () => {
    try {
      await client.negotiate();
      transportLabel = client.activeTransport ?? "auto";
    } catch {
      transportLabel = "auto (WS probe failed → REST+SSE)";
    }
    await refreshAll();
  });

  async function refreshAll() {
    cards = SEED_SYMBOLS.map((s) => ({ symbol: s, quote: null, loading: true, error: null }));
    const symbols = cards.map((c) => c.symbol);
    try {
      if (fetchMode === "bulk") {
        // One method, one roundtrip: Market.GetQuotes(symbols) → Quote[] (unknown symbols skipped).
        const res = await client.call(client.market.getQuotes(symbols));
        if (res.code === 200 && res.data) {
          // The server skips unknown symbols; align cards by symbol, mark missing as unknown.
          const bySymbol = new Map(res.data.map((q) => [q.symbol, q]));
          cards = cards.map((c) => {
            const q = bySymbol.get(c.symbol) ?? null;
            return { symbol: c.symbol, quote: q, loading: false, error: q ? null : `unknown symbol "${c.symbol}"` };
          });
        } else {
          cards = cards.map((c) => ({ ...c, loading: false, error: res.error?.message ?? `HTTP ${res.code}` }));
        }
      } else {
        // One roundtrip, N existing GetQuote calls — no server method needed. The generated
        // Batch builder is Serial (designed for @alias chaining, chapter 6); for independent
        // fan-out, Serial still means one roundtrip — the server just sequences the calls.
        const b = new Batch();
        for (const s of symbols) b.add(client.market.getQuote(s)).named(s);
        const responses = await client.batch(b);
        cards = cards.map((c, i) => {
          const r = responses[i];
          if (r && r.code === 200 && r.data === null) return { symbol: c.symbol, quote: null, loading: false, error: `unknown symbol "${c.symbol}"` };
          if (r && r.code === 200) return { symbol: c.symbol, quote: r.data as Quote, loading: false, error: null };
          return { symbol: c.symbol, quote: null, loading: false, error: r?.error?.message ?? `HTTP ${r?.code}` };
        });
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      cards = cards.map((c) => ({ ...c, loading: false, error: msg }));
    }
  }

  async function addSymbol() {
    const symbol = newSymbol.trim().toUpperCase();
    if (!symbol) return;
    if (cards.some((c) => c.symbol === symbol)) {
      newSymbol = "";
      return;
    }
    newSymbol = "";
    // Adding one symbol = one GetQuote call (a batch of one buys nothing here).
    const card: Card = { symbol, quote: null, loading: true, error: null };
    cards = [...cards, card];
    try {
      const res = await client.call(client.market.getQuote(symbol));
      if (res.code === 200 && res.data === null) card.error = `unknown symbol "${symbol}"`;
      else if (res.code === 200) card.quote = res.data;
      else card.error = res.error?.message ?? `HTTP ${res.code}`;
    } catch (e) {
      card.error = e instanceof Error ? e.message : String(e);
    } finally {
      card.loading = false;
    }
  }

  async function runChain() {
    const q = chainQuery.trim();
    if (!q) { chainError = "Enter a search query."; return; }
    chainLoading = true;
    chainError = null;
    chainSymbols = [];
    chainQuotes = [];
    try {
      // One Batch (Serial — the only mode that resolves @alias). Add the provider first,
      // Exposes its $[*] as "symbols"; then the consumer GetQuotes, whose `symbols` param
      // is the provider's alias value (compile-time-typed as string[] via the path record).
      // One client.batch(b) = one SleipnirMultiRequest = one roundtrip over `auto`.
      const b = new Batch();
      const search = b.add(client.market.search(q)).exposes("$[*]", "symbols").named("search");
      b.add(client.market.getQuotes(search.alias("symbols"))).named("quotes");
      const responses = await client.batch(b);
      const searchRes = responses[0];
      const quotesRes = responses[1];
      if (searchRes.code === 200 && searchRes.data) chainSymbols = searchRes.data as string[];
      if (quotesRes.code === 200 && quotesRes.data) chainQuotes = quotesRes.data as Quote[];
      if (chainSymbols.length === 0)
        chainError = `No symbols matched "${q}". Try: bit, eth, sol, doge, coin, o.`;
    } catch (e) {
      chainError = e instanceof Error ? e.message : String(e);
    } finally {
      chainLoading = false;
    }
  }

  async function login() {
    loggingIn = true;
    loginError = null;
    try {
      // Account.Login returns the SleipnirResponse envelope; its data is { token, profile }. The
      // generator emits `unknown` for a SleipnirResponse return type, so read the data by shape.
      const res = await client.call(client.account.login(loginUser, loginPass));
      if (res.code !== 200 || !res.data) {
        loginError = res.error?.message ?? `HTTP ${res.code}`;
        return;
      }
      const payload = res.data as { token: string; profile: Profile };
      profile = payload.profile;
      // Arm every bundled backend with the bearer, then pin REST+SSE — the browser WS handshake
      // can't carry Authorization, so authed calls over `auto` (WS) would 401. REST+SSE can.
      client.setBearer(payload.token);
      await client.useTransport("rest");
      transportLabel = "rest+sse (authed)";
      await loadHoldings();
    } catch (e) {
      loginError = e instanceof Error ? e.message : String(e);
    } finally {
      loggingIn = false;
    }
  }

  async function logout() {
    client.setBearer("");
    profile = null;
    holdings = [];
    holdingsError = null;
    // Back to `auto` for the anonymous Market board — probe WS again.
    try {
      await client.useTransport("auto");
      await client.negotiate();
      transportLabel = client.activeTransport ?? "auto";
    } catch {
      transportLabel = "auto (WS probe failed → REST+SSE)";
    }
  }

  async function loadHoldings() {
    loadingHoldings = true;
    holdingsError = null;
    holdings = [];
    try {
      // Portfolio.GetHoldings is [SleipnirAuthorise]-gated → needs the bearer we just set.
      // Without it (or before login) the server returns 401 and the client throws.
      const res = await client.call(client.portfolio.getHoldings());
      if (res.code === 200 && res.data) holdings = res.data as Holding[];
      else holdingsError = res.error?.message ?? `HTTP ${res.code}`;
    } catch (e) {
      holdingsError = e instanceof Error ? e.message : String(e);
    } finally {
      loadingHoldings = false;
    }
  }

  // Chapter 9 feed handlers. The handlers object is the generated `SubscribeHandlers<PriceTick>`
  // ({ onNext, onError?, onComplete? }). `onNext` fires per tick on the client's pump; we keep a
  // rolling window of prices for the sparkline and mirror the framework's lastEventId for resume.
  // All PriceTick fields are optional on the wire (discovery carries no nullability), so narrow.
  function feedHandlers(): { onNext: (t: PriceTick) => void; onError?: (err: Error) => void; onComplete?: () => void } {
    return {
      onNext: (t) => {
        const price = t.price ?? 0;
        const change = t.change ?? 0;
        priceSeries = [...priceSeries, price].slice(-60);
        feedTicks = [...feedTicks, t].slice(-20);
        if (feedSub) feedLastEventId = feedSub.lastEventId ?? feedLastEventId;
        feedStatus = `${feedTicks.length} tick(s) · last ${t.symbol ?? "?"} = $${price} (${change >= 0 ? "+" : ""}${change})`;
      },
      onError: (e) => { feedStatus = `feed error: ${e.message}`; },
      onComplete: () => { feedStatus = "feed completed."; },
    };
  }

  async function subscribeFeed() {
    if (feedSub) return;
    feedBusy = true; feedStatus = null; resumeStatus = null;
    try {
      // Pin the chosen transport, then subscribe. `auto` probes WS (the feed works over WS
      // anonymous — no browser-Authorization limitation here because it's not authed). `rest`
      // is the REST+SSE "best friends" path (SSE rides on rest); `signalr` is the hub path.
      await client.useTransport(feedTransport);
      feedSub = await client.priceFeed.ticks("BTC", feedHandlers());
      feedSubId = feedSub.subscriptionId;
      feedLastEventId = feedSub.lastEventId ?? 0;
      feedStatus = `subscribed (${feedTransport}) · id ${feedSubId.slice(0, 8)}… · waiting for ticks…`;
    } catch (e) {
      feedStatus = `subscribe failed: ${e instanceof Error ? e.message : String(e)}`;
      feedSub = null;
    } finally {
      feedBusy = false;
    }
  }

  function unsubscribeFeed() {
    feedSub?.unsubscribe();
    feedSub = null;
    feedStatus = "unsubscribed.";
  }

  async function dropAndResume() {
    // Cross-transport resume: capture the durable subscriptionId + the last eventId the client
    // processed, drop the current handle, then resume over SSE. The server's durable
    // SleipnirSubscriptionStore is process-wide, so a subscriptionId created over WS/signalr is
    // resumable over SSE. NB: a clean WS `unsubscribe()` sends `kind:"unsubscribe"` which DESTROYS
    // the durable subscription → the SSE resume gets 410 and terminates (no fresh fallback in pure
    // resume). To see a real gap-replay, subscribe over `rest` (SSE) first — its unsubscribe just
    // closes the HTTP stream, preserving the durable buffer for 60s. The chapter walks through both.
    if (!feedSub) { resumeStatus = "Subscribe first."; return; }
    const subId = feedSub.subscriptionId;
    const cursor = feedSub.lastEventId ?? 0;
    feedSub.unsubscribe();
    feedSub = null;
    resumeStatus = `dropped ${subId.slice(0, 8)}… @ eventId ${cursor}; resuming over SSE…`;
    feedStatus = null;
    try {
      // SSE rides on the `rest` profile; resume goes straight to the SSE backend's resume endpoint.
      await client.useTransport("rest");
      const resumed = await client.sse!.resume(subId, cursor, feedHandlers());
      feedSub = resumed;
      feedSubId = resumed.subscriptionId;
      // Same id → the server replayed the gap (durable survived); a 410 throws before we get here.
      resumeStatus = `resumed over SSE · SAME id ${subId.slice(0, 8)}… · gap replayed from eventId ${cursor}.`;
    } catch (e) {
      resumeStatus = `resume failed (410 = durable gone/destroyed): ${e instanceof Error ? e.message : String(e)}`;
    }
  }

  function trend(q: Quote | null): string {
    if (!q || q.change === undefined || q.change === null) return "flat";
    if (q.change > 0) return "up";
    if (q.change < 0) return "down";
    return "flat";
  }

  function formatPrice(q: Quote | null): string {
    if (q?.price === undefined || q?.price === null) return "—";
    return q.price.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  }

  // Map a rolling window of prices to SVG polyline points (viewBox 600×120). The min/max range
  // spans the visible window so the line fills the height; a flat window draws a centered line.
  function sparkPoints(prices: number[]): string {
    const W = 600, H = 120, pad = 6;
    const min = Math.min(...prices), max = Math.max(...prices);
    const span = max - min || 1;
    return prices
      .map((p, i) => {
        const x = (i / (prices.length - 1 || 1)) * (W - pad * 2) + pad;
        const y = H - pad - ((p - min) / span) * (H - pad * 2);
        return `${x.toFixed(1)},${y.toFixed(1)}`;
      })
      .join(" ");
  }
</script>

<main>
  <h1>Story Portal</h1>
  <p class="muted">Sleipnir unified transport — live market quotes</p>
  <p class="transport">transport: <strong>{transportLabel}</strong></p>

  <fieldset class="mode">
    <legend>Fetch (chapter 5)</legend>
    <label><input type="radio" name="mode" value="batch" bind:group={fetchMode} onchange={refreshAll} /> Batch — N×GetQuote, one roundtrip</label>
    <label><input type="radio" name="mode" value="bulk" bind:group={fetchMode} onchange={refreshAll} /> Bulk — single GetQuotes call</label>
  </fieldset>

  <form onsubmit={(e) => { e.preventDefault(); addSymbol(); }}>
    <input type="text" bind:value={newSymbol} placeholder="Symbol, e.g. ADA" />
    <button type="submit">Add</button>
    <button type="button" onclick={refreshAll}>Refresh all</button>
  </form>

  <div class="board">
    {#each cards as card (card.symbol)}
      <div class="card">
        <h2>{card.symbol}</h2>
        {#if card.loading}
          <p class="muted">loading…</p>
        {:else if card.error}
          <p class="err">{card.error}</p>
        {:else}
          <p class="price {trend(card.quote)}">${formatPrice(card.quote)}</p>
          <p class="muted {trend(card.quote)}">
            {#if card.quote?.change !== undefined && card.quote?.change !== null}
              {card.quote.change > 0 ? "+" : ""}{card.quote.change}
            {/if}
          </p>
          {#if card.quote?.time}
            <p class="muted">{new Date(card.quote.time).toLocaleTimeString()}</p>
          {/if}
        {/if}
      </div>
    {/each}
  </div>

  <section class="chain">
    <h2>Chain — Search → GetQuotes, one roundtrip (chapter 6)</h2>
    <p class="muted">
      <code>Search(q)</code> exposes <code>$[*]</code> (all matched tickers) as <code>@symbols</code>;
      <code>GetQuotes(@symbols)</code> consumes it. Two calls, one roundtrip, no client glue.
    </p>
    <form onsubmit={(e) => { e.preventDefault(); runChain(); }}>
      <input type="text" bind:value={chainQuery} placeholder="Search, e.g. bit / eth / o" />
      <button type="submit" disabled={chainLoading}>{chainLoading ? "Chaining…" : "Chain"}</button>
    </form>
    {#if chainError}
      <p class="err">{chainError}</p>
    {:else if chainSymbols.length || chainQuotes.length}
      <p class="muted small">
        matched: <code>[{chainSymbols.join(", ")}]</code> → {chainQuotes.length} quote(s)
      </p>
      <ul class="chain-results">
        {#each chainQuotes as q (q.symbol)}
          <li><strong>{q.symbol}</strong> ${formatPrice(q)} <span class="muted {trend(q)}">({q.change != null && q.change > 0 ? "+" : ""}{q.change})</span></li>
        {/each}
      </ul>
    {/if}
  </section>

  <section class="auth">
    <h2>Auth — customer login (chapter 8)</h2>
    <p class="muted">
      A browser WebSocket can&rsquo;t set <code>Authorization</code>, so after login the portal
      pins <strong>REST+SSE</strong> for authed calls (<code>setBearer</code> +
      <code>useTransport("rest")</code>) — the server-side admin keeps <code>auto</code>.
    </p>

    {#if profile}
      <p class="ok">
        Logged in as <strong>{profile.username}</strong> (role: {profile.role}).
        transport: <code>{authedTransportLabel()}</code>
      </p>
      <button type="button" onclick={logout}>Log out</button>

      <h3 class="h6">Portfolio.GetHoldings (authed)</h3>
      <button type="button" onclick={loadHoldings} disabled={loadingHoldings}>
        {loadingHoldings ? "Loading…" : "Load holdings"}
      </button>
      {#if holdingsError}
        <p class="err">{holdingsError}</p>
      {:else if holdings.length}
        <table class="holdings">
          <thead><tr><th>Symbol</th><th>Qty</th><th>Avg price</th></tr></thead>
          <tbody>
            {#each holdings as h (h.symbol)}
              <tr><td>{h.symbol}</td><td>{h.quantity}</td><td>{h.averagePrice}</td></tr>
            {/each}
          </tbody>
        </table>
      {/if}
    {:else}
      <form onsubmit={(e) => { e.preventDefault(); login(); }}>
        <input type="text" bind:value={loginUser} placeholder="customer" />
        <input type="password" bind:value={loginPass} placeholder="customer" />
        <button type="submit" disabled={loggingIn}>{loggingIn ? "Logging in…" : "Log in"}</button>
      </form>
      {#if loginError}
        <p class="err">{loginError}</p>
      {/if}
      <p class="muted small">Try customer / customer. (admin / admin works too, but the feed controls live in the Blazor admin.)</p>
    {/if}
  </section>

  <section class="feed">
    <h2>Live feed — PriceFeed.Ticks, server push (chapter 9)</h2>
    <p class="muted">
      <code>priceFeed.ticks("BTC", handlers)</code> subscribes to the <code>[SleipnirEvent]</code>
      feed. The admin-only <code>StartFeed</code>/<code>StopFeed</code> control whether ticks are
      produced; the feed itself is anonymous, so the portal can subscribe before login. Pick a
      transport, subscribe, and watch the BTC random-walk draw itself. Drop &amp; resume over SSE
      replays the gap from the durable store (subscribe over <code>sse</code> first to see a real
      replay — a clean WS unsubscribe destroys the durable subscription).
    </p>

    <fieldset class="mode">
      <legend>Feed transport</legend>
      <label><input type="radio" name="ftransport" value="auto" bind:group={feedTransport} /> auto (WebSocket)</label>
      <label><input type="radio" name="ftransport" value="rest" bind:group={feedTransport} /> rest (REST+SSE — best friends)</label>
      <label><input type="radio" name="ftransport" value="signalr" bind:group={feedTransport} /> signalr</label>
    </fieldset>

    <div class="feed-controls">
      <button type="button" onclick={subscribeFeed} disabled={feedBusy || feedSub !== null}>
        {feedBusy ? "Subscribing…" : "Subscribe (BTC)"}
      </button>
      <button type="button" onclick={unsubscribeFeed} disabled={!feedSub}>Unsubscribe</button>
      <button type="button" onclick={dropAndResume} disabled={!feedSub}>Drop &amp; resume (SSE)</button>
    </div>

    {#if feedStatus}<p class="muted small">{feedStatus}</p>{/if}
    {#if resumeStatus}<p class="ok small">{resumeStatus}</p>{/if}
    {#if feedSubId}<p class="muted small">subscriptionId <code>{feedSubId}</code> · lastEventId {feedLastEventId}</p>{/if}

    {#if priceSeries.length > 1}
      <svg class="spark" viewBox="0 0 600 120" preserveAspectRatio="none" aria-hidden="true">
        <polyline fill="none" stroke="currentColor" stroke-width="1.5"
          points={sparkPoints(priceSeries)} />
      </svg>
    {/if}

    {#if feedTicks.length > 0}
      <table class="ticks">
        <thead><tr><th>Time</th><th>Symbol</th><th>Price</th><th>Change</th></tr></thead>
        <tbody>
          {#each feedTicks as t (t.time ?? `${t.price}-${feedTicks.indexOf(t)}`)}
            <tr><td>{t.time ? new Date(t.time).toLocaleTimeString() : "—"}</td><td>{t.symbol ?? "?"}</td>
              <td>${t.price ?? 0}</td>
              <td class={(t.change ?? 0) >= 0 ? "up" : "down"}>{(t.change ?? 0) >= 0 ? "+" : ""}{t.change ?? 0}</td></tr>
          {/each}
        </tbody>
      </table>
    {/if}
  </section>
</main>