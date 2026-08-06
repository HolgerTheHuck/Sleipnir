# TrameServerTest

A minimal Trame server created from the `trame-server` template.

## Run

```bash
dotnet run --launch-profile https
```

## Endpoints

- Developer UI: `https://localhost:5001/Trame`
- REST API: `https://localhost:5001/api/trame/json`
- WebSocket: `wss://localhost:5001/tramews`
- SignalR: `https://localhost:5001/tramehub`

## Next steps

- Add more `[TrameController]` / `[TrameMethod]` classes to define your API.
- Call the API from a C#, TypeScript, or Svelte client.
