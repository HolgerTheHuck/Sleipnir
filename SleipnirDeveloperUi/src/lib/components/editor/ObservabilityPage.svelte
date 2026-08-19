<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import { fetchObservability, type ObservabilitySnapshot } from '../../api/client';

  // Live observability panel. Polls the opt-in GET /api/sleipnir/observability
  // endpoint every POLL_MS while the tab is active (the component is mounted only
  // for the active observability tab → onMount/onDestroy gate the interval). Keeps
  // a short ring-buffer history per series for the sparklines. The endpoint is
  // RequireAuth-gated like /discovery; a 401/non-2xx surfaces as an error banner.

  const POLL_MS = 2000;
  const HISTORY = 60;

  let snapshot = $state<ObservabilitySnapshot | null>(null);
  let error = $state<string | null>(null);
  let loading = $state(false);
  let lastUpdated = $state<number | null>(null);
  let connsHist = $state<number[]>([]);
  let subsHist = $state<number[]>([]);
  let droppedHist = $state<number[]>([]);
  let timer: ReturnType<typeof setInterval> | null = null;

  function pushHist(arr: number[], v: number): void {
    arr.push(v);
    if (arr.length > HISTORY) arr.shift();
  }

  async function refresh(): Promise<void> {
    loading = true;
    try {
      const snap = await fetchObservability();
      snapshot = snap;
      error = null;
      lastUpdated = Date.now();
      pushHist(connsHist, snap.activeConnections);
      pushHist(subsHist, snap.activeSubscriptions);
      // Dropped is cumulative — store the delta-per-poll so the sparkline shows the
      // drop rate, not a monotonic ramp. First sample has no baseline → 0.
      const prev = droppedHist.length > 0 ? droppedHist[droppedHist.length - 1] : snap.eventDroppedTotal;
      pushHist(droppedHist, Math.max(0, snap.eventDroppedTotal - prev));
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  onMount(() => {
    void refresh();
    timer = setInterval(() => void refresh(), POLL_MS);
  });

  onDestroy(() => {
    if (timer) clearInterval(timer);
  });

  function formatUptime(ms: number): string {
    const s = Math.floor(ms / 1000);
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = s % 60;
    if (h > 0) return `${h}h ${m}m`;
    if (m > 0) return `${m}m ${sec}s`;
    return `${sec}s`;
  }

  // Sparkline: normalize a series to bar heights (0..100%). A flat-zero series still
  // renders a minimal baseline so the row is visible.
  function barHeight(value: number, max: number): number {
    if (max <= 0) return 4;
    return Math.max(4, Math.round((value / max) * 100));
  }

  let connsMax = $derived(Math.max(1, ...connsHist));
  let subsMax = $derived(Math.max(1, ...subsHist));
  let droppedMax = $derived(Math.max(1, ...droppedHist));
</script>

<div class="obs-page">
  <div class="pane-header">
    <span class="label">Observability</span>
    <span class="hint">
      {#if lastUpdated}
        updated {new Date(lastUpdated).toLocaleTimeString()}{loading ? ' · …' : ''}
      {:else}
        {loading ? 'loading…' : 'idle'}
      {/if}
    </span>
    <div class="actions">
      <button class="ghost small" onclick={() => void refresh()} disabled={loading} title="Jetzt aktualisieren">
        {loading ? 'Refreshing…' : 'Refresh'}
      </button>
    </div>
  </div>

  {#if error}
    <div class="obs-error">
      <strong>Observability nicht verfügbar.</strong>
      <span>{error}</span>
      <p class="hint">
        Der Endpoint <code>GET /api/sleipnir/observability</code> ist opt-in
        (<code>SleipnirOptions.EnableObservability = true</code>) und wie <code>/discovery</code>
        RequireAuth-gated. Für einen Prometheus-Scrape stattdessen
        <code>AddSleipnirPrometheusMetrics</code> + <code>UseSleipnirPrometheusScrapingEndpoint</code>
        aus <code>Sleipnir.Telemetry</code> verwenden.
      </p>
    </div>
  {/if}

  {#if snapshot}
    <div class="obs-section">
      <div class="obs-section-label">Transports</div>
      <div class="transport-pills">
        <span class="pill success">REST</span>
        <span class="pill" class:success={snapshot.transports.webSocket} class:warning={!snapshot.transports.webSocket}>
          WebSocket {snapshot.transports.webSocket ? 'on' : 'off'}
        </span>
        <span class="pill" class:success={snapshot.transports.signalR} class:warning={!snapshot.transports.signalR}>
          SignalR {snapshot.transports.signalR ? 'on' : 'off'}
        </span>
        <span class="pill" class:success={snapshot.transports.sse} class:warning={!snapshot.transports.sse}>
          SSE {snapshot.transports.sse ? 'on' : 'off'}
        </span>
      </div>
    </div>

    <div class="obs-grid">
      <div class="metric-card">
        <div class="metric-label">Active WS connections</div>
        <div class="metric-value">{snapshot.activeConnections}</div>
        <div class="sparkline">
          {#each connsHist as v, i (i)}<span class="bar" style="height:{barHeight(v, connsMax)}%"></span>{/each}
        </div>
      </div>
      <div class="metric-card">
        <div class="metric-label">Active subscriptions</div>
        <div class="metric-value">{snapshot.activeSubscriptions}</div>
        <div class="sparkline">
          {#each subsHist as v, i (i)}<span class="bar" style="height:{barHeight(v, subsMax)}%"></span>{/each}
        </div>
      </div>
      <div class="metric-card">
        <div class="metric-label">Events dropped (total)</div>
        <div class="metric-value warn">{snapshot.eventDroppedTotal}</div>
        <div class="sparkline">
          {#each droppedHist as v, i (i)}<span class="bar drop" style="height:{barHeight(v, droppedMax)}%"></span>{/each}
        </div>
      </div>
    </div>

    <div class="obs-section">
      <div class="obs-section-label">Cumulative</div>
      <table class="obs-table">
        <tbody>
          <tr><th>Calls</th><td>{snapshot.callCount}</td></tr>
          <tr><th>Errors</th><td class="err">{snapshot.errorCount}</td></tr>
          <tr><th>Batches</th><td>{snapshot.batchCount}</td></tr>
          <tr><th>Uptime</th><td>{formatUptime(snapshot.uptimeMs)}</td></tr>
        </tbody>
      </table>
    </div>

    <p class="hint metrics-hint">
      Für den vollen Metrik-Satz (p50/p90-Latenz, Batch-Fan-Out, Tags) per OTel-Exporter
      scrapen: <code>GET /api/sleipnir/metrics</code> (Prometheus-Text) via
      <code>Sleipnir.Telemetry</code>. Heimdall (embeddable OTel-Stack) kann diesen
      Producer später ersetzen — das Prometheus-Text-Interface bleibt der Vertrag.
    </p>
  {:else if !error}
    <div class="obs-empty">Loading live runtime state…</div>
  {/if}
</div>

<style>
  .obs-page {
    display: flex;
    flex-direction: column;
    gap: 12px;
    height: 100%;
    min-height: 0;
    overflow-y: auto;
    padding-right: 4px;
  }
  .obs-error {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 12px;
    border: 1px solid var(--error);
    border-radius: var(--radius-sm);
    background: rgba(248, 81, 73, 0.08);
    color: var(--text);
    font-size: 0.85rem;
  }
  .obs-error .hint {
    margin-top: 4px;
    line-height: 1.5;
  }
  .obs-error code,
  .metrics-hint code {
    font-family: var(--font-mono);
    font-size: 0.78rem;
    background: var(--bg-overlay);
    padding: 1px 4px;
    border-radius: 3px;
  }
  .obs-section {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .obs-section-label {
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.5px;
    color: var(--text-dim);
    font-weight: 700;
  }
  .transport-pills {
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
  }
  .obs-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    gap: 10px;
  }
  .metric-card {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 10px 12px;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--bg-elevated);
  }
  .metric-label {
    font-size: 0.72rem;
    color: var(--text-muted);
    text-transform: uppercase;
    letter-spacing: 0.4px;
  }
  .metric-value {
    font-family: var(--font-mono);
    font-size: 1.5rem;
    font-weight: 700;
    color: var(--text);
  }
  .metric-value.warn {
    color: var(--warning);
  }
  .sparkline {
    display: flex;
    align-items: flex-end;
    gap: 1px;
    height: 28px;
    margin-top: 2px;
  }
  .sparkline .bar {
    flex: 1;
    min-width: 0;
    background: var(--accent-secondary);
    border-radius: 1px;
    opacity: 0.85;
  }
  .sparkline .bar.drop {
    background: var(--warning);
  }
  .obs-table {
    width: 100%;
    max-width: 320px;
    border-collapse: collapse;
    font-size: 0.85rem;
  }
  .obs-table th {
    text-align: left;
    font-weight: 600;
    color: var(--text-muted);
    padding: 4px 8px 4px 0;
    border-bottom: 1px solid var(--border-muted);
  }
  .obs-table td {
    font-family: var(--font-mono);
    color: var(--text);
    padding: 4px 0;
    border-bottom: 1px solid var(--border-muted);
    text-align: right;
  }
  .obs-table td.err {
    color: var(--error);
  }
  .obs-empty {
    color: var(--text-muted);
    font-size: 0.9rem;
    padding: 24px 0;
  }
  .metrics-hint {
    font-size: 0.75rem;
    color: var(--text-dim);
    line-height: 1.5;
    margin-top: 4px;
  }
</style>