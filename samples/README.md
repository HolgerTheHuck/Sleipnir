# Sleipnir Samples

> Neu hier? Der Schritt-für-Schritt-Einstieg (null → DevUI) steht in
> [`../GETTING_STARTED.md`](../GETTING_STARTED.md). Diese Samples hier sind die
> ausführbare Referenz — NuGet/npm-basiert, gegen denselben Server.

Ausführbare, sauber kommentierte Code-Beispiele als Entwickler-Referenz. Jeder
der drei Projekte (Server, C#-Client, TS-Client) läuft eigenständig und zieht
Sleipnir aus dem **lokalen NuGet-/npm-Paket** — kein Build der Solution nötig. Pro
Sprache gibt es vier Szenarien, die unterschiedliche RPC-Muster zeigen.

## Szenarien

| # | Szenario | Was es zeigt |
|---|----------|--------------|
| 01 | Single Call | Ein einzelner RPC-Aufruf (fluent + raw; REST **und** WebSocket) |
| 02 | Batch Parallel | Mehrere unabhängige Aufrufe in einer Roundtrip — `ExecutionMode.Parallel` |
| 03 | Batch Serial | Mehrere Aufrufe nacheinander — `ExecutionMode.Serial` |
| 04 | Dependencies | Chaining: ein Aufruf gibt einen Wert weiter (`@alias`), der nächste nutzt ihn |

## Aufbau

```
samples/
  server/                     # Ausführbarer Beispiel-Server (consumiert Sleipnir.Server als NuGet-Paket)
    SampleServer.csproj       # Web-SDK, net8.0 — PackageReference auf Sleipnir.Server
    Program.cs                 # AddSleipnir → UseSleipnirTransports → MapSleipnir (3 Zeilen Wiring)
    SampleServer.cs            # Controller + DTOs + In-Memory-Store
    nuget.config               # lokaler Feed → ../../artifacts/packages
  csharp/                      # Ausführbare C#-Client-Samples (SleipnirClient aus lokalem Feed)
    Samples.csproj             # Console, net8.0 — PackageReference auf Sleipnir.Client
    Program.cs                 # Runner: Szenario-Arg 1–4 / all
    Dtos.cs                    # client-seitige Customer/Order-POCOs + SampleJson-Options
    01-single-call.cs  02-batch-parallel.cs  03-batch-serial.cs  04-dependency-chain.cs
    nuget.config               # lokaler Feed → ../../artifacts/packages
  typescript/                  # Ausführbare TS-Client-Samples (sleipnir-client aus clients/ts)
    package.json               # type:module; sleipnir-client via file:../../clients/ts
    run.ts                     # Runner: Szenario-Arg 1–4 / all (Node Type-Stripping)
    01-single-call.ts  02-batch-parallel.ts  03-batch-serial.ts  04-dependency-chain.ts
    tsconfig.json              # für typecheck (npm run typecheck)
```

## Schnellstart (low-barrier)

Alle drei Projekte laufen gegen denselben Server auf `https://localhost:5001`.

```bash
# 0) Einmalig pro Maschine: vertrauenswürdiges HTTPS-Dev-Cert (für wss://)
dotnet dev-certs https --trust

# 1) Sleipnir-Pakete ins lokale Feed legen (aus Repository-Root)
dotnet pack Sleipnir.sln -c Release -o artifacts/packages

# 2) Server starten (eigenes Terminal — läuft blockierend)
dotnet run --project samples/server/SampleServer.csproj
```

Endpunkte nach dem Start:
- **REST**: `POST https://localhost:5001/api/sleipnir/json` (+ `/multi`, `GET /discovery`)
- **WebSocket**: `wss://localhost:5001/sleipnirws`
- **Developer-UI**: `https://localhost:5001/Sleipnir` — Browser-Konsole über die Live-Discovery:
  Calls in mehreren Tabs offen halten, Batches/`@alias`-Ketten visuell bauen (mit statischem
  Checker), TS-/C#-Code generieren, History, und den kompletten Arbeitsstand als Snapshot
  speichern/wiederherstellen. Siehe [README_DETAILS.md → Developer UI](../README_DETAILS.md#developer-ui).

### C#-Client-Samples

```bash
dotnet run --project samples/csharp -- all   # alle 4 Szenarien nacheinander
dotnet run --project samples/csharp -- 1     # nur Szenario 1 (auch 2, 3, 4)
```

### TypeScript-Client-Samples

```bash
cd samples/typescript
npm install                  # einmalig: lokaler sleipnir-client + ws
npm start                    # alle 4 Szenarien nacheinander
npm run start:1              # nur Szenario 1 (auch :2 :3 :4)
# oder direkt:  node --experimental-strip-types run.ts 4
```

> Hinweis: Node vertraut dem ASP.NET-Dev-Cert nicht (eigener CA-Store). Der
> TS-Runner setzt daher prozessweit `NODE_TLS_REJECT_UNAUTHORIZED=0` — nur für
> Dev-Samples, niemals in Produktion.

## Voraussetzungen (für eigene Setups)

- **Server**: `dotnet add package Sleipnir.Server` (bringt alle Transporte transitiv),
  dann `builder.Services.AddSleipnir(...)` + `app.UseSleipnirTransports()` + `app.MapSleipnir()`.
  Siehe `server/Program.cs` für das Wiring und `server/SampleServer.cs` für die gezeigten
  Controller. Zum sofortigen Start: obiger Schnellstart-Block.
- **C#-Client**: `dotnet add package Sleipnir.Client`.
- **TS-Client**: `npm install sleipnir-client` (lokales Paket: `clients/ts`).

## Kernkonzepte kurz

- **Vertrag = C#-Klassen** (`[SleipnirController]` / `[SleipnirMethod]`) — kein `.proto`, keine IDL.
- **Parameter** werden als `Params`-JsonArray (`{ parameterName, data }`-Einträge,
  `data` nativer JSON-Wert) gesendet, serverseitig nach Name gebunden.
  `CancellationToken` injiziert der Server automatisch.
- **JsonPath ist ergebnisrelativ**: `$` ist der serialisierte Rückgabewert der Methode
  (z. B. ein `int` oder ein `Customer`-Objekt) — **kein** `$.data`-Envelope.
  `$.Id` → Eigenschaft, `$[0].Id` → erstes Listenelement.
- **`@alias`**: Ein downstream-Aufruf nutzt `@alias` als Parameterwert; der Server
  löst es gegen die `ExposedDependencies` vorheriger Aufrufe auf. Die `DependencyMapping`
  (`alias → JsonPath`) deklariert, was ein Aufruf bereitstellt.
- **Mode**: `Parallel` = `Task.WhenAll` (keine Aliase). `Serial` = sequenziell, löst
  `@alias` auf. **Achtung**: sobald irgendein Request ein `DependencyMapping` hat,
  schaltet der Server automatisch auf topologische Batch-Ausführung — der `Mode` wird
  dann ignoriert. Für reine Chaining-Beispiele empfehlen wir trotzdem `Serial`.
- **Fehler**: Business-/Domänenfehler → `SleipnirResults.NotFound(...)` etc. zurückgeben
  (Code + Message erreichen den Client). Unerwartete Exceptions → generisches 500
  (Message-Leak nur mit `EnableDetailedErrors`).

> Alle Beispiele sind ausführbar und gegen den lokalen Server getestet. Die
> Client-Base-URLs (`https://localhost:5001`) und die In-Memory-Daten im Server
> sind Platzhalter für eigene Setups.