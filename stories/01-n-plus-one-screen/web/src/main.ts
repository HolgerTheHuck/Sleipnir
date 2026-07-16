// Story 01 — The N+1 Screen. This UI consumes the trame-codegen-generated typed
// client (src/api/, produced by scripts/gen.mjs). Two ways to assemble the same
// order-detail screen:
//   1. "6 serial roundtrips" — six sequential awaits (order → customer → address
//      → lines → articles → stock). StoryLatency is 30ms/call → ~180ms.
//   2. "1 typed Trame batch" — one roundtrip; the client declares the dependency
//      graph (exposes/alias), the server resolves it topologically → ~30ms.
//
// `new TrameClient("/")` issues same-origin relative /api/trame/json calls; Vite
// proxies /api → http://localhost:5001 (the Story-01 server). No CORS, no .NET
// changes.
import "./style.css";
import {
  TrameClient,
  Batch,
  type Order,
  type Customer,
  type Address,
  type OrderLine,
  type Article,
  type StockInfo,
} from "./api/index.js";
import type { TrameResponse } from "trame-client";

const client = new TrameClient("/");

interface LoadedData {
  mode: "serial" | "batch";
  ms: number;
  order: Order | null;
  customer: Customer | null;
  address: Address | null;
  lines: OrderLine[];
  articles: Article[];
  stock: StockInfo[];
}

const timings: { serial?: number; batch?: number } = {};

// --- DOM scaffolding ----------------------------------------------------------
const app = document.getElementById("app")!;

app.innerHTML = `
  <header>
    <h1>Trame Story 01 — The N+1 Screen</h1>
    <p class="lede">One order-detail screen, six dependent reads. Load it two ways and compare the roundtrips.</p>
    <p class="lede"><a href="/Trame">Open the Trame Developer UI ↗</a> to explore the contract and send raw calls.</p>
  </header>
  <section class="controls">
    <button id="serial-btn" class="primary">Load — 6 serial roundtrips</button>
    <button id="batch-btn" class="accent">Load — 1 typed Trame batch</button>
  </section>
  <div id="status" class="status"></div>
  <div id="timing" class="timing"></div>
  <div id="detail" class="detail"></div>
`;

const serialBtn = document.getElementById("serial-btn")! as HTMLButtonElement;
const batchBtn = document.getElementById("batch-btn")! as HTMLButtonElement;
const statusEl = document.getElementById("status")!;
const timingEl = document.getElementById("timing")!;
const detailEl = document.getElementById("detail")!;

// --- Serial: six sequential roundtrips ---------------------------------------
async function loadSerial(): Promise<LoadedData> {
  const t0 = performance.now();
  const order = await client.call(client.order.getById(42));
  const customerId = order.data?.customerId;
  const addressId = order.data?.shippingAddressId;
  const orderId = order.data?.id;
  const customer = await client.call(client.customer.getById(customerId!));
  const address = await client.call(client.address.getById(addressId!));
  const lines = await client.call(client.orderLine.getByOrder(orderId!));
  const articleIds = (lines.data ?? []).map((l) => l.articleId!);
  const articles = await client.call(client.article.getByIds(articleIds));
  const stock = await client.call(client.stock.getByArticles(articleIds));
  const ms = performance.now() - t0;
  return {
    mode: "serial",
    ms,
    order: order.data,
    customer: customer.data,
    address: address.data,
    lines: lines.data ?? [],
    articles: articles.data ?? [],
    stock: stock.data ?? [],
  };
}

// --- Batch: one typed Trame roundtrip (server resolves the graph) -------------
async function loadBatch(): Promise<LoadedData> {
  const t0 = performance.now();
  const batch = new Batch();
  const order = batch.add(client.order.getById(42))
    .exposes("$.customerId", "@customerId")
    .exposes("$.id", "@orderId")
    .exposes("$.shippingAddressId", "@addressId");
  batch.add(client.customer.getById(order.alias("@customerId")));
  const lines = batch.add(client.orderLine.getByOrder(order.alias("@orderId")))
    .exposes("$[*].articleId", "@articleIds");
  // Diamond: two consumers of the same array alias.
  batch.add(client.article.getByIds(lines.alias("@articleIds")));
  batch.add(client.stock.getByArticles(lines.alias("@articleIds")));
  batch.add(client.address.getById(order.alias("@addressId")));

  const responses: TrameResponse[] = await client.batch(batch);
  // Responses return in topological order, not request order — match by id.
  const byId = new Map(responses.map((r) => [r.id, r]));
  const ms = performance.now() - t0;
  return {
    mode: "batch",
    ms,
    order: (byId.get("Order.GetById")?.data as Order | null) ?? null,
    customer: (byId.get("Customer.GetById")?.data as Customer | null) ?? null,
    address: (byId.get("Address.GetById")?.data as Address | null) ?? null,
    lines: (byId.get("OrderLine.GetByOrder")?.data as OrderLine[] | null) ?? [],
    articles: (byId.get("Article.GetByIds")?.data as Article[] | null) ?? [],
    stock: (byId.get("Stock.GetByArticles")?.data as StockInfo[] | null) ?? [],
  };
}

// --- Rendering ---------------------------------------------------------------
function renderTiming(): void {
  const s = timings.serial;
  const b = timings.batch;
  if (s === undefined && b === undefined) {
    timingEl.innerHTML = "";
    return;
  }
  const speedup = s !== undefined && b !== undefined ? (s / b).toFixed(1) : null;
  timingEl.innerHTML = `
    <div class="timing-row">
      <div class="timing-card ${s === undefined ? "pending" : ""}"><span class="label">6 serial roundtrips</span><span class="ms">${s !== undefined ? s.toFixed(0) + " ms" : "—"}</span></div>
      <div class="timing-card ${b === undefined ? "pending" : ""}"><span class="label">1 typed batch</span><span class="ms">${b !== undefined ? b.toFixed(0) + " ms" : "—"}</span></div>
      ${speedup ? `<div class="timing-card speedup"><span class="label">speedup</span><span class="ms">${speedup}×</span></div>` : ""}
    </div>`;
}

function renderDetail(d: LoadedData): string {
  const o = d.order;
  if (!o) return `<p class="empty">No order loaded.</p>`;
  const stockByArticle = new Map(d.stock.map((s) => [s.articleId, s.inStock]));
  const articleById = new Map(d.articles.map((a) => [a.id, a]));
  const lineRows = d.lines
    .map((l) => {
      const art = articleById.get(l.articleId);
      const inStock = stockByArticle.get(l.articleId);
      return `<tr>
        <td>${art?.name ?? "—"}</td>
        <td class="num">${l.qty ?? 0}</td>
        <td class="num">${art?.price != null ? "$" + art.price.toFixed(2) : "—"}</td>
        <td class="num ${inStock != null && inStock > 0 ? "ok" : "out"}">${inStock != null ? inStock : "—"}</td>
      </tr>`;
    })
    .join("");
  return `
    <div class="card">
      <div class="card-head">
        <h2>Order #${o.id ?? "?"}</h2>
        <span class="badge">${d.mode === "batch" ? "1 batch" : "6 serial"} · ${d.ms.toFixed(0)} ms</span>
      </div>
      <dl class="kv">
        <dt>Customer</dt><dd>${d.customer?.name ?? "—"}</dd>
        <dt>Status</dt><dd>${o.status ?? "—"}</dd>
        <dt>Placed</dt><dd>${o.placedAt ?? "—"}</dd>
        <dt>Ship to</dt><dd>${d.address ? `${d.address.street ?? ""}, ${d.address.zip ?? ""} ${d.address.city ?? ""}` : "—"}</dd>
      </dl>
      <table class="lines">
        <thead><tr><th>Article</th><th class="num">Qty</th><th class="num">Price</th><th class="num">In stock</th></tr></thead>
        <tbody>${lineRows || `<tr><td colspan="4" class="empty">No lines.</td></tr>`}</tbody>
      </table>
    </div>`;
}

function setStatus(msg: string, kind: "info" | "error" = "info"): void {
  statusEl.innerHTML = msg ? `<p class="${kind}">${msg}</p>` : "";
}

function setBusy(busy: boolean): void {
  serialBtn.disabled = busy;
  batchBtn.disabled = busy;
}

async function run(mode: "serial" | "batch"): Promise<void> {
  setBusy(true);
  setStatus(`Loading via ${mode === "batch" ? "1 typed batch" : "6 serial roundtrips"}…`);
  try {
    const data = mode === "serial" ? await loadSerial() : await loadBatch();
    timings[mode] = data.ms;
    detailEl.innerHTML = renderDetail(data);
    renderTiming();
    setStatus(`Loaded in ${data.ms.toFixed(0)} ms via ${mode === "batch" ? "1 batch" : "6 serial calls"}.`);
  } catch (err) {
    setStatus(`Error: ${(err as Error).message}. Is the Story-01 server running on :5001?`, "error");
  } finally {
    setBusy(false);
  }
}

serialBtn.addEventListener("click", () => void run("serial"));
batchBtn.addEventListener("click", () => void run("batch"));