# HelloSleipnir

The smallest possible Sleipnir server — a drop-in sample that gets you from zero to a working RPC call in minutes.

## Run

```bash
# Trust the ASP.NET Core dev certificate once per machine
dotnet dev-certs https --trust

# Start the server
dotnet run --launch-profile https
```

Open `https://localhost:5001/Sleipnir` to use the Developer UI, or call the API directly:

```bash
curl -k -X POST https://localhost:5001/api/sleipnir/json \
  -H "Content-Type: application/json" \
  -d '{"controller":"Greeting","method":"Hello","params":[{"parameterName":"name","data":"Sleipnir"}],"id":"1"}'
```

Expected response:

```json
{
  "id": "1",
  "isSuccess": true,
  "data": "Hello, Sleipnir! Welcome to Sleipnir."
}
```

## What it shows

- One `[SleipnirController]` / `[SleipnirMethod]` class.
- Three lines of wiring: `AddSleipnir` → `UseSleipnirTransports` → `MapSleipnir`.
- The Sleipnir Developer UI at `/Sleipnir`.
