# SleipnirServerTemplate

A minimal Sleipnir server created from the `sleipnir-server` template.

## Run

```bash
dotnet run --launch-profile https
```

## Endpoints

- Developer UI: `https://localhost:5001/Sleipnir`
- REST API: `https://localhost:5001/api/sleipnir/json`
- WebSocket: `wss://localhost:5001/sleipnirws`
- SignalR: `https://localhost:5001/sleipnirhub`

## Next steps

- Add more `[SleipnirController]` / `[SleipnirMethod]` classes to define your API.
- Call the API from a C#, TypeScript, or Svelte client.
