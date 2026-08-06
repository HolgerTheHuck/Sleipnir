# TrameServerSpaTemplate

A Trame server with a Svelte 5 single-page application.

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

- Developer UI: `https://localhost:5001/Trame`
- REST API: `https://localhost:5001/api/trame/json`
- WebSocket: `wss://localhost:5001/tramews`
- SignalR: `https://localhost:5001/tramehub`

## Project layout

- `Program.cs` — Trame wiring and CORS for the SPA dev server.
- `GreetingController.cs` — sample `[TrameController]` / `[TrameMethod]` API.
- `web/` — Svelte 5 + Vite + `trame-client` client application.
