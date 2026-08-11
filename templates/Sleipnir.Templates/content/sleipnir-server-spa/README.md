# SleipnirServerSpaTemplate

A Sleipnir server with a Svelte 5 single-page application.

## Run

```bash
# Restore npm packages and start the Vite dev server
cd web
npm install
npm run dev

# In another terminal, start the ASP.NET Core server
cd ..
dotnet run --launch-profile https
```

The server proxies API requests to `https://localhost:5001`.

## Endpoints

- Developer UI: `https://localhost:5001/Sleipnir`
- REST API: `https://localhost:5001/api/sleipnir/json`
- WebSocket: `wss://localhost:5001/sleipnirws`
- SignalR: `https://localhost:5001/sleipnirhub`

## Project layout

- `Program.cs` — Sleipnir wiring and CORS for the SPA dev server.
- `GreetingController.cs` — sample `[SleipnirController]` / `[SleipnirMethod]` API.
- `web/` — Svelte 5 + Vite + `sleipnir-client` client application.
