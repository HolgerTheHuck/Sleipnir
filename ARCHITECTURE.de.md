# Trame Architektur

> English version: [ARCHITECTURE.md](ARCHITECTURE.md)

## Design-Philosophie

Trame entstand aus einer einfachen Beobachtung: **REST ist für Ressourcen gemacht, nicht für RPC.**

Wenn man Aktionen aufruft (nicht CRUD auf Substantiven), zwingt REST in unpassende Muster:
- URL-Pfade, die Substantive simulieren: `/api/customer/42/add-address`
- N+1-Roundtrips für abhängige Aufrufe: Client wartet auf A, ruft dann B auf, dann C
- Kein Batching: 10 Methodenaufrufe = 10 HTTP-Requests mit 10× Overhead

Trame lässt sich von **GraphQL** (Abhängigkeitsauflösung, Batch-Queries) und **gRPC** (methodenorientierte Aufrufe, binärer Transport) inspirieren, bleibt aber im .NET/ASP.NET-Core-Ökosystem.

### Warum nicht gRPC?

Zwei wesentliche Gründe, warum Trame existiert statt einfach gRPC zu nutzen:

1. **gRPC war nicht gut unterstützt** zur Entstehungszeit. Browser-Unterstützung erforderte gRPC-Web-Proxys, und die .NET-gRPC-Tooling war noch unausgereift. Trame unterstützt REST, WebSocket und SignalR out-of-the-box — Browser können WebSocket oder REST nutzen, ohne Proxys.

2. **Code-first, nicht schema-first.** gRPC erfordert `.proto`-Dateien — eine separate IDL, die C#-Stubs generiert. Das bedeutet: eine zweite Source-of-Truth pflegen — die `.proto` und die C#-Implementierung. Trame nutzt Attribute auf normalen C#-Klassen: `[TrameController]`, `[TrameMethod]`, `[TrameDataContract]`. Der C#-Code *ist* der Vertrag. Discovery-Metadaten werden zur Laufzeit generiert, nicht zur Compile-Zeit.

Die Kern-Designprinzipien:
1. **Methodenorientiert**: Controller und Methoden, nicht Routen und Verben
2. **Batch als Standard**: Mehrere Aufrufe in einem Roundtrip, parallel oder seriell
3. **Serverseitige Abhängigkeitsauflösung**: `@alias`-Chaining inspiriert von GraphQL-Field-Selection
4. **Transport-agnostisch**: Derselbe `TrameRequest` funktioniert über REST, WebSocket oder SignalR
5. **Zero-Reflection-Ausführung**: Expression Trees kompilieren Methodenaufrufe beim Startup
6. **Code-first**: Keine IDL, keine `.proto`-Dateien, keine Code-Generierung — C#-Klassen sind das Schema

## Übersicht

Trame ist eine protokoll-agnostische RPC-Engine, die zwischen der Geschäftslogik und mehreren Transportschichten sitzt. Die Kern-Engine (`TrameCore`) löst Methodenaufrufe über vorkompilierte Expression Trees auf, während die Transports (REST, WebSocket, SignalR) das Wire-Protokoll handhaben.

## Anfrage/Antwort-Fluss

### Einzelner Aufruf
1. Der Client erstellt einen `TrameRequest` mit Controller-Name, Methoden-Name und JSON-Parametern
2. Der Transport sendet die Anfrage (HTTP POST, WebSocket-Frame oder SignalR-Hub-Aufruf)
3. `TrameInvoker` sucht Controller und Methode im Invoke-Cache
4. Interceptor-Pipeline läuft (Logging, Tracing, etc.)
5. Autorisierung wird geprüft (`[TrameAuthorise]`)
6. Parameter werden aus JSON deserialisiert und nach Name gematcht
7. Methode wird über vorkompilierten Expression-Tree-Delegate aufgerufen
8. Ergebnis wird als JSON serialisiert und in `TrameResponse` verpackt
9. Antwort wird über den Transport zurückgesendet

### Batch-Aufruf (Multi-Request)
1. Client sendet `TrameMultiRequest` mit mehreren `TrameRequest`-Einträgen und einem `ExecutionMode`
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
6. Der Server schützt sich selbst vor Kardinalitäts-Sprengung über zwei Caps in `TrameOptions` — `MaxParameterArrayLength` (Default 1000) und `MaxResultElementCount` (Default 10000), jeweils `0` = unbegrenzt. Body-Size-Limits decken den server-generierten Passthrough nicht ab; diese Caps tun das. Details siehe PROTOCOL.md → Limits.

## Kernkomponenten

### TrameInvoker (`TrameCore`)
- Singleton-Service, thread-sicher
- `ConcurrentDictionary`-Caches: Controller-Typen und kompilierte `InvokeInfo`
- `CompileInvocation()`: Expression Trees erstellen `Func<object, object?[], object?>` pro Methode
- `BuildParameters()`: Deserialisiert JSON-Parameter, matcht nach Parameternamen, injiziert `CancellationToken`
- `ExecuteMethod()`: Erstellt DI-Scope, löst Controller-Instanz auf, ruft kompilierten Delegate auf
- Behandelt synchrone, asynchrone (Task/Task<T>), void und `IAsyncEnumerable<T>`-Rückgabetypen

### TrameDiscoveryService (`TrameCore`)
- Generiert `DiscoveryInfo` mit allen Controllern, Methoden, Parametern und Typen
- Typen werden als strukturierte, sprachneutrale `TypeRef`-Objekte ausgegeben (`kind` ∈ `scalar | array | set | map | ref | stream | opaque | void`), nicht als .NET-Typnamen-Strings — versioniert über ein rein-additives `discoveryVersion`-Feld. Autoritative Spezifikation: [`docs/discovery-schema.md`](docs/discovery-schema.md). Enums registrieren als `TypeMeta` mit `kind:"enum"` + `members`; eine Verwendungsstelle ist `{kind:"ref", ref:"<enumKey>"}`. Trame serialisiert Enums als deren zugrundeliegenden Integer, ein Enum-Ref ist also wire-numerisch (die `members` sind reine Dokumentation).
- Typen werden per **Signatur-Inferenz** einbezogen: jeder Klassentyp aus einer Controller-Assembly wird voll expandiert (Property-Schema, Beispiel, Nested-Types); `[TrameDataContract]` ist optionaler Override (bare = force-expand, `Exclude = true` = force-opaque). Typen aus anderen Assemblies (BCL, Trame-Envelope, Fremdlibs) bleiben opaque.
- Extrahiert `[TrameDocumentation]`-Zusammenfassungen und `[TrameExample]`-JSON-Beispiele
- Gecacht mit Invalidierung bei neuen Registrierungen

### DependencyGraphBuilder (`TrameCore`)
- Topologische Sortierung der Requests basierend auf `DependencyMapping` und `@alias`-Verwendung
- Gruppiert unabhängige Requests in parallele Batches (Level-basiert)
- Zykluserkennung wirft `InvalidOperationException` mit beteiligten Request-IDs

### Interceptor-Pipeline (`TrameCore`)
- `ITrameInterceptor`: `InvokeAsync(request, next, ct)`
- Pipeline umschließt die Methodenausführung in umgekehrter Reihenfolge
- Eingebaut: `TrameLoggingInterceptor` (misst Aufrufdauer)
- Eigene Interceptors via DI registrieren: `services.AddSingleton<ITrameInterceptor, MyInterceptor>()`

### Transportschichten

| Transport | Projekt | Wire-Protokoll | Features |
|-----------|---------|---------------|----------|
| REST | TrameRest | HTTP/1.1 + JSON | Minimal APIs unter `/api/trame/json`, `/api/trame/json/multi`, `/api/trame/discovery` |
| WebSocket | TrameWebSocket | RFC 6455 + JSON-Text-Frames | Erkennt Single vs. Multi-Request automatisch, Multi-Frame-Support |
| SignalR | TrameHub | WebSocket + MessagePack | Hub-Methoden `DoWork()` / `DoWorkMany()`, Auto-Reconnect |

### Client-Bibliothek (`TrameClient`)
- `ITrameClient`-Interface: `Call(TrameRequest)`, `Call<T>(TrameRequest)`, `Call(TrameMultiRequest)`
- `TrameRestJsonClient`: HTTP-basiert, Connection-Pooling, `IDisposable`
- `TrameWebSocketClient`: Persistente Verbindung, `SemaphoreSlim` für Thread-Safety, `IAsyncDisposable`
- `TrameSignalrClient`: Auto-Reconnect mit exponentiellem Backoff, MessagePack-Protokoll
- `TrameCall`: Fluent Builder mit `.Named()`, `.Exposes()`, `.WithAlias()`, `.With()`, `.Add()`, `.ToRequest()`

## Fehlermodell

```csharp
TrameResponse {
    int Code;                    // HTTP-ähnlicher Statuscode
    string? Data;                // JSON-Ergebnis oder Fehlermeldung
    byte[]? Content;             // Binärer Payload
    string? Id;                  // Request-Korrelations-ID
    Dictionary<string, string>? ExposedDependencies;  // Für Chaining
    TrameError? Error;            // Strukturierter Fehler (wenn Code != 200)
    bool IsSuccess;               // true bei 200-299
}

TrameError {
    int Code;
    string Message;
    string? Details;             // Stacktrace nur in Development
    string? RequestId;
}
```

Clients werfen `TrameException` mit `TrameError` bei non-2xx-Antworten.

### Fehler aus einem Controller zurückgeben

Zwei Wege — nicht austauschbar:

- **Business-/Domain-Fehler → `TrameResponse` zurückgeben** (empfohlen). Der Invoker
  reicht eine zurückgegebene `TrameResponse` unverändert durch
  (`TrameInvoker.ReturnResponse`: `if (result is TrameResponse) return it;`), sodass
  `Code` + `Data` + `Error` 1:1 beim Client ankommen. Fabrik `TrameResults`
  (`TrameCommon.Results`) belegt `Data` (Mensch-lesbare Message) **und** das
  strukturierte `TrameError` konsistent:
  ```csharp
  using TrameCommon.Results;
  [TrameMethod("GetById")]
  public TrameResponse GetById(int id)
  {
      var c = _repo.Find(id);
      if (c is null) return TrameResults.NotFound($"Customer '{id}' not found.");
      return TrameResults.Ok(c);
  }
  ```
  API: `Ok(object?|string|byte[])`, `NoContent()`, `Error(code, message, details?)`,
  Convenience `BadRequest`/`Unauthorized`/`NotFound`/`Conflict`/`InternalServerError`,
  RFC-7807-Überladung `Error(ProblemDetails)`. Die Message ist **nicht** an
  `EnableDetailedErrors` gekoppelt — sie kommt in jeder Umgebung beim Client an.
- **Unerwartetes/internes Versagen → werfen**. Jede Exception wird zu `500` mit
  **generischer** Message (kein Leak); der Stacktrace steht nur in `Error.Details`,
  wenn `EnableDetailedErrors` an ist (Development). Für Validierung/"not found" also
  falsch — der Client sähe nur das generische 500. `TrameException` zu werfen
  **propagiert keinen Code** (der Server hat kein `catch(TrameException)`); Code per
  `TrameResults.Error(...)` steuern.

## Attribute

| Attribut | Ziel | Zweck |
|-----------|--------|---------|
| `[TrameController("name")]` | Klasse | Markiert eine Klasse als RPC-Controller |
| `[TrameMethod("name")]` | Methode | Markiert eine Methode als remote aufrufbar |
| `[TrameAuthorise]` | Methode | Erfordert Authentifizierung (optionale Rolle) |
| `[TrameDataContract]` | Klasse | Optionaler Discovery-Override (bare = force-expand / `Exclude = true` = force-opaque); Default ist Signatur-Inferenz über die Controller-Assembly-Grenze |
| `[TrameDocumentation("summary")]` | Klasse/Methode/Parameter | XML-ähnliche Doku für Discovery |
| `[TrameExample("json")]` | Klasse | Beispiel-JSON für Developer-UI |

## Erweiterungspunkte

1. **Custom Interceptor**: `ITrameInterceptor` implementieren, in DI registrieren
2. **Custom Transport**: `ITrameClient` (Client) oder Middleware (Server) implementieren
3. **Custom Autorisierung**: `TrameAuthoriseAttribute.OnAuthorization()` erweitern
4. **Discovery-Erweiterung**: `TrameDiscoveryService.BuildDiscoveryInfo()` erweitern