# Getting Started — from zero to the Trame Developer UI

A step-by-step walk from an empty directory to a running Trame server with the Developer UI in
the browser, your first call made in the UI, and the same call made from a C# client. No prior
Trame knowledge assumed; .NET basics assumed.

> This guide is the **canonical wiring** (`AddTrame` → `UseTrameTransports` → `MapTrame`). For the
> feature tour see [README.md](README.md); for the wire format see [PROTOCOL.md](PROTOCOL.md);
> for the worked stories see [docs/stories/](docs/stories/); for north-bound hardening see
> [SECURITY_GUIDE.md](SECURITY_GUIDE.md).

---

## 0. Prerequisites

- **.NET 8 SDK** — `dotnet --version` must report 8.x. Install from <https://dotnet.microsoft.com>.
- **A dev HTTPS cert** (only if you'll use `https://` / `wss://` locally):
  ```bash
  dotnet dev-certs https --trust
  ```
- **A Trame reference.** Either a `PackageReference` to the published `Trame.Server` NuGet
  package (once published), or — inside this repo — a `ProjectReference` to
  `TrameServer/TrameServer.csproj`. This guide uses the repo ProjectReference so it runs today,
  with no pack step.

---

## 1. Create a minimal web project

```bash
dotnet new web -n MyTrameServer
cd MyTrameServer
```

Add a reference to the Trame server (all transports + DevUI in one project). From inside
`MyTrameServer/`:

```bash
# Option A — published package (when available):
dotnet add package Trame.Server

# Option B — repo ProjectReference (this repo, no pack step):
dotnet add reference ../path/to/TrameServer/TrameServer.csproj
```

`Trame.Server` brings `TrameHub`, `TrameRest`, `TrameWebSocket`, and the DevUI transitively.

---

## 2. Wire Trame in `Program.cs`

Replace `Program.cs` with the canonical three-line wiring:

```csharp
using TrameHub.Extensions;
using TrameServer;

var builder = WebApplication.CreateBuilder(args);

// Serve the DevUI bundles from the neighboring TrameDeveloperUi project in Development.
// (In Production/Publish you need the StaticWebAssets manifest + UseStaticFiles instead —
//  for getting started, Development is what you want.)
builder.WebHost.UseStaticWebAssets();

builder.Services.AddTrame(o =>
{
    o.UseSignalR = true;          // REST + WebSocket are always on; SignalR is the opt-in third wire
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

var app = builder.Build();

app.UseStaticFiles();      // actually serves the DevUI static assets
app.UseRouting();

app.UseTrameTransports();  // WebSocket (default channel) + controller registration (auto-discovery)
app.MapTrame();            // REST (/api/trame) + DevUI (/Trame) + SignalR hub (/tramehub, UseSignalR=true)

app.Run();
```

Three calls do the work: `AddTrame` registers the engine and auto-discovers your controllers,
`UseTrameTransports` wires WebSocket and triggers controller registration, `MapTrame` maps the
REST endpoints, the DevUI, and the SignalR hub. SignalR/telemetry are **optional** — the minimum
is `AddTrame` + `UseTrameTransports` + `MapTrame` with no options.

> `UseStaticWebAssets()` is a Development-mode hook that serves the DevUI's unbuilt assets from
> the neighboring `TrameDeveloperUi` project. It is why F5 "just works" in the repo. For a
> published app you'd `dotnet publish` (which produces the static-asset manifest) and serve
> them with `UseStaticFiles` — but that's a later concern.

---

## 3. Add a controller

The contract **is** the C# class. Drop this into the project (e.g. `HelloController.cs`):

```csharp
using TrameCore.Attributes;

[TrameController("Hello")]
public class HelloController
{
    [TrameMethod("Greet")]
    public Greeting Greet(string name) => new() { Message = $"Hello, {name}!", Length = name.Length };
}

public class Greeting
{
    public string Message { get; set; } = "";
    public int Length { get; set; }
}
```

`[TrameController]` and `[TrameMethod]` are the only attributes you need. Trame auto-discovers
this class from the assembly at startup — no manual registration. No `.proto`, no IDL, no code
generation.

---

## 4. Run it and open the DevUI

```bash
dotnet run
```

Watch the console for the listening URL (e.g. `http://localhost:5000`). Open the browser at:

```
http://localhost:5000/Trame
```

That is the **Developer UI** — a working console over the live discovery, not a Swagger page.
You'll see the `Hello` controller with its `Greet` method and the `Greeting` contract type tree,
generated from the code you just wrote.

> The DevUI lives at **`/Trame`**. A bare `/` does not redirect there unless you add a redirect
> (the story templates do, for F5 comfort: `app.MapGet("/", c => { c.Response.Redirect("/Trame"); return Task.CompletedTask; });`). Optional, but nice for local dev.

---

## 5. Make your first call (in the DevUI)

1. Click **`Hello.Greet`** in the left tree. A new tab opens with the parameter form.
2. Fill in `name` → `World`.
3. Click **Send**. The response appears in the right panel:
   ```json
   { "message": "Hello, World!", "length": 5 }
   ```
4. Try the **Batch & Dependency Builder** to chain a second call off the first, or **Codegen** to
   emit the C# / TypeScript for the call you just made. Tabs and history persist across reloads.

The DevUI is reading `GET /api/trame/discovery` — the same metadata any auto-generated client
would consume.

---

## 6. Make the same call from a C# client

In a second project (or the Trame sample client), reference `Trame.Client` and:

```csharp
using TrameClient.Trame;
using TrameCommon.Models;

var client = new TrameRestJsonClient("http://localhost:5000/");

var request = TrameCall.Init("Hello", "Greet").With("World").ToRequest();
var greeting = await client.Call<Greeting>(request);

Console.WriteLine(greeting!.Message);   // Hello, World!
```

Swap `TrameRestJsonClient` for `TrameWebSocketClient` or `TrameSignalrClient` — same `ITrameClient`
interface, same call, different wire. That is the multi-transport thesis in one line.

---

## 7. Verify with curl

Trame's native REST is **envelope-at-200**: the HTTP status is always `200` and the real status
lives in the body's `code` field.

```bash
curl -s -X POST http://localhost:5000/api/trame/json \
  -H 'Content-Type: application/json' \
  -d '{"controller":"Hello","method":"Greet","params":[{"parameterName":"name","data":"World"}],"id":"q1"}'
```

```json
{"code":200,"data":{"message":"Hello, World!","length":5},"content":null,"error":null,"id":"q1"}
```

`code:200` + the `data` payload — that is a successful Trame call on the wire.

---

## Where to go next

- **Build-time contract & typed clients** (Node-free): [CODEGEN_ONBOARDING.md](CODEGEN_ONBOARDING.md)
  — the server exports `contract.trame.json` (drift fails the build), the client is generated from
  it by a Roslyn source generator. The compile-time boundary gRPC gives you, without `.proto`.
- **Dependency chaining** (the defining feature): [docs/stories/01-the-n-plus-one-screen.md](docs/stories/01-the-n-plus-one-screen.md)
  — six dependent reads, one roundtrip.
- **Command fan-out with isolation**: [docs/stories/02-one-button-seven-commands.md](docs/stories/02-one-button-seven-commands.md)
- **Three transports, one contract**: [docs/stories/03-the-same-contract-three-wires.md](docs/stories/03-the-same-contract-three-wires.md)
- **Going north-bound (untrusted clients)**: [docs/stories/04-north-bound-security.md](docs/stories/04-north-bound-security.md)
  + [SECURITY_GUIDE.md](SECURITY_GUIDE.md) — flip `RequireAuthentication`, set rate limits and batch caps.
- **Runnable samples** (NuGet/npm-based): [samples/README.md](samples/README.md).
- **Full feature reference**: [README_DETAILS.md](README_DETAILS.md).
- **Wire format**: [PROTOCOL.md](PROTOCOL.md).