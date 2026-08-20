# The Sleipnir Guide — a progressive, runnable 10-chapter tutorial

> Clone the repo, open `guide/`, follow the chapters. Each step leaves the project in a
> runnable state — you build on the **same growing solution**, not throwaway folders.

This guide builds a realistic **3-tier app** around one cohesive domain — a crypto
**Market / Exchange** — in ten short chapters. By the end you have a Sleipnir API
serving a Blazor admin backend and a Svelte customer portal, with batching, dependency
chaining, JWT auth, and a **live BTC price feed** streaming over Sleipnir events.

```
                 ┌──────────────────────────┐
   admin bearer  │  guide/admin  Story.Admin │  Blazor Server (port 5011)
   ────────────▶│  Pflege-Backend           │  generated C# client
                 └────────────┬─────────────┘
                              │ Sleipnir (REST + WS + SSE + SignalR)
                 ┌────────────▼─────────────┐
                 │  guide/server  Story.Api  │  ASP.NET Minimal API (port 5010)
                 │  the Sleipnir API          │  DevUI at /Sleipnir
                 └────────────▲─────────────┘
                              │ Sleipnir (REST + SSE — proxy-safe, browser-auth-friendly)
   customer     ┌────────────┴─────────────┐
   bearer  ────▶│  guide/portal  Story.Portal│  Svelte 5 + Vite (dev port 5173)
                 │  Endkunden-Portal         │  generated TS client
                 └──────────────────────────┘
```

A third client — `guide/web/`, a plain HTML/JS page with zero build step — is introduced
in chapter 3 to show how thin the wire really is.

## Sleipnir & REST — best friends

Sleipnir is a multi-transport RPC framework, but this guide has a deliberate lean into
**REST + SSE** as the robust, proxy-safe, curl-friendly default:

- The **Developer UI** and `curl` talk plain REST — your first call in chapter 1 is a
  one-line HTTP POST, no SDK.
- The **HTML/JS client** (chapter 3) is REST + SSE only, and that's a *choice*: it dodges
  the browser WebSocket auth limitation and works behind any corporate proxy.
- **Authed browser calls** (chapter 8) use REST + SSE for the same reason.
- The unified transport's `auto` mode probes WebSocket and **falls back to REST + SSE**
  transparently (chapters 4 and 9) — REST is the safety net, not a lesser path.

WebSocket and SignalR are first-class and shown too (chapter 9 turns the hub on for
binary-efficient streaming), but REST + SSE is the friend you can always reach for.

## Prerequisites

- **.NET 8 SDK** (the repo targets `net8.0`; runs on .NET 8+).
- **Node 18+** for the web (chapter 3) and Svelte portal (chapter 4) clients.
- A one-time `dotnet dev-certs https --trust` for the `https://localhost:5010` dev URL.
- Everything is wired with **in-repo project references** — no NuGet or npm restore of
  Sleipnir packages is needed. `npm install` is only required for the two JS clients.

## Chapters

| # | Chapter | What you build |
|---|---------|----------------|
| 1 | [Onboarding](chapters/01-onboarding.md) | First server, `Market.GetQuote`, DevUI, `curl`. |
| 2 | [Blazor client (C# codegen)](chapters/02-blazor-client.md) | Pflege-Backend, generated typed C# client. |
| 3 | [HTML/JS client (codegen, zero build)](chapters/03-html-js.md) | Plain page, import map, REST + SSE. |
| 4 | [Svelte portal (TS codegen)](chapters/04-svelte-portal.md) | Endkunden-Portal, unified transport `auto`. |
| 5 | [Batching](chapters/05-batching.md) | `GetQuotes`, Parallel vs Serial, one roundtrip. |
| 6 | [Chaining](chapters/06-chaining.md) | `Search → GetQuotes` via `@alias`, list fan-out. |
| 7 | [LINQ provider](chapters/07-linq.md) | `Dep<T>` + `SleipnirQuery<T>` — typed ergonomic layer over `@alias`. |
| 8 | [Auth — JWT Bearer](chapters/08-auth.md) | `Account.Login`, `[SleipnirAuthorise]`, admin vs customer, 401 vs 403. |
| 9 | [Eventing — live BTC feed](chapters/09-events.md) | `[SleipnirEvent]`, Svelte live chart, Blazor monitor, resume. |
| 10 | [Production](chapters/10-production.md) | Interceptors, `/metrics` + `/observability`, tracing, binary. _(planned)_ |

Each chapter assumes the previous one's project state. The final repo state is the
complete runnable 3-tier app.

## How the guide is structured

One **growing solution**, `Story.sln`:

```
guide/
  README.md          ← you are here
  Story.sln          ← Story.Api + Story.Admin (the .NET projects)
  server/  Story.Api  ← the Sleipnir API (grows each chapter)
  admin/   Story.Admin ← Blazor Pflege-Backend (from chapter 2)
  web/               ← plain HTML/JS client (chapter 3, npm, zero build)
  portal/ Story.Portal← Svelte Endkunden-Portal (chapter 4, npm)
  chapters/01..10-*.md ← the narrative, one per step
```

Start the API once and leave it running:

```bash
dotnet run --project guide/server
# REST        https://localhost:5010/api/sleipnir/json
# DevUI       https://localhost:5010/Sleipnir
# WebSocket   wss://localhost:5010/sleipnirws
```

Then follow chapter 1 → 8. Each chapter ends with a **"Try it"** block: start the
client the chapter introduced, see the result.

## Where this fits

The guide is the *end-to-end walkthrough*. For reference material, see
[`GETTING_STARTED.md`](../GETTING_STARTED.md), [`PROTOCOL.md`](../PROTOCOL.md),
[`CODEGEN_ONBOARDING.md`](../CODEGEN_ONBOARDING.md), and
[`DEPENDENCY_BINDING.md`](../DEPENDENCY_BINDING.md). The guide cross-links into those
rather than duplicating them.