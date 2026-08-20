# Sleipnir Architektur

> English version: [ARCHITECTURE.md](ARCHITECTURE.md)

## Design-Philosophie

Sleipnir entstand aus einer einfachen Beobachtung: **REST ist für Ressourcen gemacht, nicht für RPC.**

Wenn man Aktionen aufruft (nicht CRUD auf Substantiven), zwingt REST in unpassende Muster:
- URL-Pfade, die Substantive simulieren: `/api/customer/42/add-address`
- N+1-Roundtrips für abhängige Aufrufe: Client wartet auf A, ruft dann B auf, dann C
- Kein Batching: 10 Methodenaufrufe = 10 HTTP-Requests mit 10× Overhead

Sleipnir lässt sich von **GraphQL** (Abhängigkeitsauflösung, Batch-Queries) und **gRPC** (methodenorientierte Aufrufe, binärer Transport) inspirieren, bleibt aber im .NET/ASP.NET-Core-Ökosystem.

### Warum nicht gRPC?

Zwei wesentliche Gründe, warum Sleipnir existiert statt einfach gRPC zu nutzen:

1. **gRPC war nicht gut unterstützt** zur Entstehungszeit. Browser-Unterstützung erforderte gRPC-Web-Proxys, und die .NET-gRPC-Tooling war noch unausgereift. Sleipnir unterstützt REST, WebSocket und SignalR out-of-the-box — Browser können WebSocket oder REST nutzen, ohne Proxys.

2. **Code-first, nicht schema-first.** gRPC erfordert `.proto`-Dateien — eine separate IDL, die C#-Stubs generiert. Das bedeutet: eine zweite Source-of-Truth pflegen — die `.proto` und die C#-Implementierung. Sleipnir nutzt Attribute auf normalen C#-Klassen: `[SleipnirController]`, `[SleipnirMethod]`, `[SleipnirDataContract]`. Der C#-Code *ist* der Vertrag. Discovery-Metadaten werden zur Laufzeit generiert, nicht zur Compile-Zeit.

Die Kern-Designprinzipien:
1. **Methodenorientiert**: Controller und Methoden, nicht Routen und Verben
2. **Batch als Standard**: Mehrere Aufrufe in einem Roundtrip, parallel oder seriell
3. **Serverseitige Abhängigkeitsauflösung**: `@alias`-Chaining inspiriert von GraphQL-Field-Selection
4. **Transport-agnostisch**: Derselbe `SleipnirRequest` funktioniert über REST, WebSocket oder SignalR
5. **Zero-Reflection-Ausführung**: Expression Trees kompilieren Methodenaufrufe beim Startup
6. **Code-first**: Keine IDL, keine `.proto`-Dateien, keine Code-Generierung — C#-Klassen sind das Schema

## Übersicht

Sleipnir ist eine protokoll-agnostische RPC-Engine, die zwischen der Geschäftslogik und mehreren Transportschichten sitzt. Die Kern-Engine (`SleipnirCore`) löst Methodenaufrufe über vorkompilierte Expression Trees auf, während die Transports (REST, WebSocket, SignalR) das Wire-Protokoll handhaben.

## Anfrage/Antwort-Fluss

### Einzelner Aufruf
1. Der Client erstellt einen `SleipnirRequest` mit Controller-Name, Methoden-Name und JSON-Parametern
2. Der Transport sendet die Anfrage (HTTP POST, WebSocket-Frame oder SignalR-Hub-Aufruf)
3. `SleipnirInvoker` sucht Controller und Methode im Invoke-Cache
4. Interceptor-Pipeline läuft (Logging, Tracing, etc.)
5. Autorisierung wird geprüft (`[SleipnirAuthorise]`)
6. Parameter werden aus JSON deserialisiert und nach Name gematcht
7. Methode wird über vorkompilierten Expression-Tree-Delegate aufgerufen
8. Ergebnis wird als JSON serialisiert und in `SleipnirResponse` verpackt
9. Antwort wird über den Transport zurückgesendet

### Batch-Aufruf (Multi-Request)
1. Client sendet `SleipnirMultiRequest` mit mehreren `SleipnirRequest`-Einträgen und einem `ExecutionMode`
2. Bei vorhandenem `DependencyMapping` schaltet die Auto-Erkennung auf topologische Batch-Ausführung um
3. **Parallel**: Alle Requests parallel via `Task.WhenAll`
4. **Serial**: Requests nacheinander mit Dependency-Auflösung
5. **Dependency-Batch**: `DependencyGraphBuilder` erstellt Ausführungs-Batches:
   - Level 0: Alle Requests ohne Dependencies → parallel
   - Level 1: Requests, die nur von Level 0 abhängen → parallel
   - Level N: Requests, die von Level N-1 abhängen → parallel
6. Jedes Level läuft parallel, Levels nacheinander

### Dependency-Chaining
1. Request A deklariert `DependencyMapping: { "alias" → "$" }` (ergebnisrelativer JSON-Path; `$` ist das ganze Resultat, `$.Eigenschaft` eine Eigenschaft, `$[0].Id` ein Listenelement)
   - Nach der Ausführung extrahiert der Server Werte aus A's Response via JsonPath
   - Extrahierte Werte werden in `ExposedDependencies: { "alias" → value }` gespeichert
2. Request B verwendet `@alias` als Parameter-Platzhalter
   - Der Server ersetzt `@alias` durch den tatsächlichen Wert aus A's `ExposedDependencies`
3. Dies ermöglicht 3 abhängige Aufrufe in einem einzigen HTTP-Roundtrip

### Grenzen der @alias-Auflösung
`@alias` ist **wert-einfach und nicht-erweiternd** (bewusste Grenze, siehe PROTOCOL.md → Limits):
1. Jeder `dependencyMapping`-Eintrag extrahiert nur den **ersten** JsonPath-Treffer — `$[*].id` liefert das erste Element, nicht alle (`DependencyResolver.ExtractValue` gibt `Matches.First()` zurück).
2. Der Server erzeugt aus einem Array-Ergebnis keine weiteren Requests — **kein serverseitiger Fan-out**. Die Kardinalität wird nie erhöht.
3. Pfade sind case-sensitiv gegen die **camelCase**-Server-Ausgabe (`$[0].id`, nicht `$[0].Id`).
4. Um eine Collection an einen Aufruf zu übergeben, expose `$` und binde an einen Collection-Typ (`int[]`/`List<T>`).
5. Für „alle nach Id laden" bevorzugt ein Batch-Get-Endpoint (`GetByIds(int[])`); ein künftiger `Map`/`ForEach`-Modus muss geboundet sein (`MaxFanOut`, beschränkte Concurrency, per-Element-Ergebnisse, Read-Only-Default).
6. Der Server schützt sich selbst vor Kardinalitäts-Sprengung über zwei Caps in `SleipnirOptions` — `MaxParameterArrayLength` (Default 1000) und `MaxResultElementCount` (Default 10000), jeweils `0` = unbegrenzt. Body-Size-Limits decken den server-generierten Passthrough nicht ab; diese Caps tun das. Details siehe PROTOCOL.md → Limits.

## Kernkomponenten

### SleipnirInvoker (`SleipnirCore`)
- Singleton-Service, thread-sicher
- `ConcurrentDictionary`-Caches: Controller-Typen und kompilierte `InvokeInfo`
- `CompileInvocation()`: Expression Trees erstellen `Func<object, object?[], object?>` pro Methode
- `BuildParameters()`: Deserialisiert JSON-Parameter, matcht nach Parameternamen, injiziert `CancellationToken`
- `ExecuteMethod()`: Erstellt DI-Scope, löst Controller-Instanz auf, ruft kompilierten Delegate auf
- Behandelt synchrone, asynchrone (Task/Task<T>), void und `IAsyncEnumerable<T>`-Rückgabetypen

### SleipnirDiscoveryService (`SleipnirCore`)
- Generiert `DiscoveryInfo` mit allen Controllern, Methoden, Parametern und Typen
- Typen werden als strukturierte, sprachneutrale `TypeRef`-Objekte ausgegeben (`kind` ∈ `scalar | array | set | map | ref | stream | opaque | void`), nicht als .NET-Typnamen-Strings — versioniert über ein rein-additives `discoveryVersion`-Feld. Autoritative Spezifikation: [`docs/discovery-schema.md`](docs/discovery-schema.md). Enums registrieren als `TypeMeta` mit `kind:"enum"` + `members`; eine Verwendungsstelle ist `{kind:"ref", ref:"<enumKey>"}`. Sleipnir serialisiert Enums als deren zugrundeliegenden Integer, ein Enum-Ref ist also wire-numerisch (die `members` sind reine Dokumentation).
- Typen werden per **Signatur-Inferenz** einbezogen: jeder Klassentyp aus einer Controller-Assembly wird voll expandiert (Property-Schema, Beispiel, Nested-Types); `[SleipnirDataContract]` ist optionaler Override (bare = force-expand, `Exclude = true` = force-opaque). Typen aus anderen Assemblies (BCL, Sleipnir-Envelope, Fremdlibs) bleiben opaque.
- Extrahiert `[SleipnirDocumentation]`-Zusammenfassungen und `[SleipnirExample]`-JSON-Beispiele
- Gecacht mit Invalidierung bei neuen Registrierungen

### DependencyGraphBuilder (`SleipnirCore`)
- Topologische Sortierung der Requests basierend auf `DependencyMapping` und `@alias`-Verwendung
- Gruppiert unabhängige Requests in parallele Batches (Level-basiert)
- Zykluserkennung wirft `InvalidOperationException` mit beteiligten Request-IDs

### Interceptor-Pipeline (`SleipnirCore`)
- `ISleipnirInterceptor`: `InvokeAsync(request, next, ct)`
- Pipeline umschließt die Methodenausführung in umgekehrter Reihenfolge
- Eingebaut: `SleipnirLoggingInterceptor` (misst Aufrufdauer)
- Eigene Interceptors via DI registrieren: `services.AddSingleton<ISleipnirInterceptor, MyInterceptor>()`

### Transportschichten

| Transport | Projekt | Wire-Protokoll | Features |
|-----------|---------|---------------|----------|
| REST | SleipnirRest | HTTP/1.1 + JSON | Minimal APIs unter `/api/sleipnir/json`, `/api/sleipnir/json/multi`, `/api/sleipnir/discovery` |
| WebSocket | SleipnirWebSocket | RFC 6455 + JSON-Text-Frames | Erkennt Single vs. Multi-Request automatisch, Multi-Frame-Support |
| SignalR | SleipnirHub | WebSocket + MessagePack | Hub-Methoden `DoWork()` / `DoWorkMany()`, Auto-Reconnect |

### Client-Bibliothek (`SleipnirClient`)
- `ISleipnirClient`-Interface: `Call(SleipnirRequest)`, `Call<T>(SleipnirRequest)`, `Call(SleipnirMultiRequest)`, sowie `SubscribeAsync<T>` / `ResumeAsync<T>` für transportübergreifende Event-Subscriptions
- `SleipnirRestJsonClient`: HTTP-basiert, Connection-Pooling, `IDisposable` (nur Calls)
- `SleipnirWebSocketClient`: Persistente Verbindung, `SemaphoreSlim` für Thread-Safety, `IAsyncDisposable` (Calls + Events)
- `SleipnirSseClient`: nur Events über `text/event-stream` mit `Last-Event-Id`-Resume, `IAsyncDisposable`
- `SleipnirSignalrClient`: Auto-Reconnect mit exponentiellem Backoff, MessagePack-Protokoll; Calls via `DoWork`/`DoWorkMany`, Events via Hub-Streaming `SubscribeAsync`
- `SleipnirTransportRouter`: einheitlicher Client, der die Backends einer Capability (`rest|ws|all|signalr`) bündelt und den Transport zur Laufzeit wählt — `auto` (Default) probiert WebSocket und fällt auf REST+SSE zurück; der generierte typisierte Client wraps einen Router
- `SleipnirCall`: Fluent Builder mit `.Named()`, `.Exposes()`, `.WithAlias()`, `.With()`, `.Add()`, `.ToRequest()`

## Fehlermodell

```csharp
SleipnirResponse {
    int Code;                    // HTTP-ähnlicher Statuscode
    string? Data;                // JSON-Ergebnis oder Fehlermeldung
    byte[]? Content;             // Binärer Payload
    string? Id;                  // Request-Korrelations-ID
    Dictionary<string, string>? ExposedDependencies;  // Für Chaining
    SleipnirError? Error;            // Strukturierter Fehler (wenn Code != 200)
    bool IsSuccess;               // true bei 200-299
}

SleipnirError {
    int Code;
    string Message;
    string? Details;             // Stacktrace nur in Development
    string? RequestId;
}
```

Clients werfen `SleipnirException` mit `SleipnirError` bei non-2xx-Antworten.

### Fehler aus einem Controller zurückgeben

Zwei Wege — nicht austauschbar:

- **Business-/Domain-Fehler → `SleipnirResponse` zurückgeben** (empfohlen). Der Invoker
  reicht eine zurückgegebene `SleipnirResponse` unverändert durch
  (`SleipnirInvoker.ReturnResponse`: `if (result is SleipnirResponse) return it;`), sodass
  `Code` + `Data` + `Error` 1:1 beim Client ankommen. Fabrik `SleipnirResults`
  (`SleipnirCommon.Results`) belegt `Data` (Mensch-lesbare Message) **und** das
  strukturierte `SleipnirError` konsistent:
  ```csharp
  using SleipnirCommon.Results;
  [SleipnirMethod("GetById")]
  public SleipnirResponse GetById(int id)
  {
      var c = _repo.Find(id);
      if (c is null) return SleipnirResults.NotFound($"Customer '{id}' not found.");
      return SleipnirResults.Ok(c);
  }
  ```
  API: `Ok(object?|string|byte[])`, `NoContent()`, `Error(code, message, details?)`,
  Convenience `BadRequest`/`Unauthorized`/`NotFound`/`Conflict`/`InternalServerError`,
  RFC-7807-Überladung `Error(ProblemDetails)`. Die Message ist **nicht** an
  `EnableDetailedErrors` gekoppelt — sie kommt in jeder Umgebung beim Client an.
- **Unerwartetes/internes Versagen → werfen**. Jede Exception wird zu `500` mit
  **generischer** Message (kein Leak); der Stacktrace steht nur in `Error.Details`,
  wenn `EnableDetailedErrors` an ist (Development). Für Validierung/"not found" also
  falsch — der Client sähe nur das generische 500. `SleipnirException` zu werfen
  **propagiert keinen Code** (der Server hat kein `catch(SleipnirException)`); Code per
  `SleipnirResults.Error(...)` steuern.

## Attribute

| Attribut | Ziel | Zweck |
|-----------|--------|---------|
| `[SleipnirController("name")]` | Klasse | Markiert eine Klasse als RPC-Controller |
| `[SleipnirMethod("name")]` | Methode | Markiert eine Methode als remote aufrufbar |
| `[SleipnirAuthorise]` | Methode | Erfordert Authentifizierung (optionale Rolle) |
| `[SleipnirDataContract]` | Klasse | Optionaler Discovery-Override (bare = force-expand / `Exclude = true` = force-opaque); Default ist Signatur-Inferenz über die Controller-Assembly-Grenze |
| `[SleipnirDocumentation("summary")]` | Klasse/Methode/Parameter | XML-ähnliche Doku für Discovery |
| `[SleipnirExample("json")]` | Klasse | Beispiel-JSON für Developer-UI |

## Erweiterungspunkte

1. **Custom Interceptor**: `ISleipnirInterceptor` implementieren, in DI registrieren
2. **Custom Transport**: `ISleipnirClient` (Client) oder Middleware (Server) implementieren
3. **Custom Autorisierung**: `SleipnirAuthoriseAttribute.OnAuthorization()` erweitern
4. **Discovery-Erweiterung**: `SleipnirDiscoveryService.BuildDiscoveryInfo()` erweitern