# HelloTrame

The smallest possible Trame server — a drop-in sample that gets you from zero to a working RPC call in minutes.

## Run

```bash
# Trust the ASP.NET Core dev certificate once per machine
dotnet dev-certs https --trust

# Start the server
dotnet run --launch-profile https
```

Open `https://localhost:5001/Trame` to use the Developer UI, or call the API directly:

```bash
curl -k -X POST https://localhost:5001/api/trame/json \
  -H "Content-Type: application/json" \
  -d '{"controller":"Greeting","method":"Hello","params":[{"parameterName":"name","data":"Trame"}],"id":"1"}'
```

Expected response:

```json
{
  "id": "1",
  "isSuccess": true,
  "data": "Hello, Trame! Welcome to Trame."
}
```

## What it shows

- One `[TrameController]` / `[TrameMethod]` class.
- Three lines of wiring: `AddTrame` → `UseTrameTransports` → `MapTrame`.
- The Trame Developer UI at `/Trame`.
