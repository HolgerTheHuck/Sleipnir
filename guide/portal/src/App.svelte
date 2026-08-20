<script lang="ts">
  import { onMount } from "svelte";
  import { client, SEED_SYMBOLS } from "./lib/api.js";
  import { Batch } from "./api/index.js";
  import type { Quote } from "./api/types.js";

  type Card = { symbol: string; quote: Quote | null; loading: boolean; error: string | null };

  let cards: Card[] = $state([]);
  let newSymbol = $state("");
  let transportLabel = $state("auto (probing…)");
  // Chapter 5: "batch" sends ONE SleipnirMultiRequest of N GetQuote calls (one roundtrip,
  // composing existing methods); "bulk" calls the single Market.GetQuotes endpoint. Chapter 4
  // did Promise.all of N calls = N roundtrips; both options below are one roundtrip.
  let fetchMode: "batch" | "bulk" = $state("batch");

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
</main>