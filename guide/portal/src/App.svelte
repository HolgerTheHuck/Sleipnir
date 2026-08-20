<script lang="ts">
  import { onMount } from "svelte";
  import { client, SEED_SYMBOLS } from "./lib/api.js";
  import { Batch } from "./api/index.js";
  import type { Quote, Profile, Holding } from "./api/types.js";

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
</main>