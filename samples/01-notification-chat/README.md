# 01 — Notification Chat

A complete end-to-end Trame sample: a notification/chat/media server, a C# console client, and a Svelte 5 SPA.

## What it demonstrates

- **Multi-transport server** — REST, WebSocket, SignalR from one `AddTrame` call.
- **Code-first contract** — `[TrameController]` / `[TrameMethod]` classes.
- **Batching** — dashboard loads unread count, chats, and gallery in one roundtrip.
- **Dependency chaining** — create a chat, send a message, then load messages using `@chatId`.
- **C# client** — `TrameRestJsonClient`, `TrameWebSocketClient`, fluent `TrameCall`.
- **TypeScript/Svelte client** — `trame-client` with Vite proxy.
- **Developer UI** — browse and test the API at `/Trame`.

## Project layout

```
samples/01-notification-chat/
  server/          ASP.NET Core + Trame.Server
  client/          C# console client (Trame.Client)
  web/             Svelte 5 + Vite + trame-client SPA
```

## Run

```bash
# 1. Start the server
cd server
dotnet run --launch-profile https

# 2. In another terminal, run the C# client
cd ../client
dotnet run

# 3. In a third terminal, run the Svelte SPA
cd ../web
npm install
npm run dev
```

Endpoints:

- Server: `https://localhost:5002`
- Developer UI: `https://localhost:5002/Trame`
- REST: `https://localhost:5002/api/trame/json`
- WebSocket: `wss://localhost:5002/tramews`
- SignalR: `https://localhost:5002/tramehub`
- SPA dev server: `https://localhost:5173`
