<script lang="ts">
  import { onMount } from "svelte";
  import { client, SEED_SYMBOLS } from "./lib/api.js";
  import type { Quote } from "./api/types.js";

  type Card = { symbol: string; quote: Quote | null; loading: boolean; error: string | null };

  let cards: Card[] = $state([]);
  let newSymbol = $state("");
  let transportLabel = $state("auto (probing…)");

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

  async function fetchOne(symbol: string): Promise<Card> {
    const card: Card = { symbol, quote: null, loading: true, error: null };
    try {
      const res = await client.call(client.market.getQuote(symbol));
      // getQuote returns null for an unknown symbol — surface it as a friendly error.
      if (res.code === 200 && res.data === null) {
        card.error = `unknown symbol "${symbol}"`;
      } else if (res.code === 200) {
        card.quote = res.data;
      } else {
        card.error = res.error?.message ?? `HTTP ${res.code}`;
      }
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

  async function addSymbol() {
    const symbol = newSymbol.trim().toUpperCase();
    if (!symbol) return;
    if (cards.some((c) => c.symbol === symbol)) {
      newSymbol = "";
      return;
    }
    newSymbol = "";
    const card = await fetchOne(symbol);
    cards = [...cards, card];
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