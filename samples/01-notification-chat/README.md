# 01 — Notification Chat

A complete end-to-end Sleipnir sample: a notification/chat/media server, a C# console client, and a Svelte 5 SPA.

## What it demonstrates

- **Multi-transport server** — REST, WebSocket, SignalR from one `AddSleipnir` call.
- **Code-first contract** — `[SleipnirController]` / `[SleipnirMethod]` classes.
- **Batching** — dashboard loads unread count, chats, and gallery in one roundtrip.
- **Dependency chaining** — create a chat, send a message, then load messages using `@chatId`.
- **C# client** — `SleipnirRestJsonClient`, `SleipnirWebSocketClient`, fluent `SleipnirCall`.
- **TypeScript/Svelte client** — `sleipnir-client` with Vite proxy.
- **Developer UI** — browse and test the API at `/Sleipnir`.

## Project layout

```
samples/01-notification-chat/
  server/          ASP.NET Core + Sleipnir.Server
  client/          C# console client (Sleipnir.Client)
  web/             Svelte 5 + Vite + sleipnir-client SPA
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
- Developer UI: `https://localhost:5002/Sleipnir`
- REST: `https://localhost:5002/api/sleipnir/json`
- WebSocket: `wss://localhost:5002/sleipnirws`
- SignalR: `https://localhost:5002/sleipnirhub`
- SPA dev server: `https://localhost:5173`
