# Trame — Security Audit & Hardening (North-Bound)

Trame wurde bis v1.0 **ausschließlich south-bound** eingesetzt: ein vertrauenswürdiger Caller
(Backend-Service, internes Tool) steuert Backend-Controller über REST/WebSocket/SignalR. Die
Vertrauensgrenze lag beim Caller. Jetzt geht Trame **north-bound**: untrusted externe Clients
treiben den Server direkt über die öffentlichen Transporte. Dieser Punkt katalogisiert die
Bedrohungsfläche für diesen neuen Betriebsmodus, was bereits gehärtet ist, was bewusst Roadmap
bleibt — und was strukturell sicher ist.

**Gültigkeitsbereich:** der Trame-Server (`TrameCore` + `TrameRest` / `TrameWebSocket` / `TrameHub`
+ `TrameServer`). Clients (`TrameClient`) und die DevUI (`TrameDeveloperUi`) sind
out-of-scope für Server-Härtung (DevUI ist ein Dev-Tool, s. F7.1).

**Bedrohungsmodell-Annahme:** der Angreifer ist ein **externer, unauthentifizierter Client** mit
Netzwerkzugriff auf die Transport-Endpunkte. Er kontrolliert Request-Body, JSON-Struktur,
`@alias`-JsonPath, Batch-Größe, Header und Connection-Rate. Er kontrolliert **nicht** den
Server-Code, die registrierten Controller oder die `TrameOptions`.

---

## TL;DR — was zu tun ist, bevor du north-bound gehst

Eine Checkliste, kein Essay. Jede Zeile ist im Detail unten begründet.

- [ ] **Authentifizierung upstream wühlen.** Trame liest `HttpContext.User` — es führt keine
      eigene Identity-Prvider-Logik. Stell ein Auth-Schema (JWT, Cookie, mTLS über Reverse-Proxy)
      ein, das `HttpContext.User` belegt, **bevor** der Trame-Transport läuft.
- [ ] **`RequireAuthentication = true`** setzen (`TrameOptions`). Default ist `false` (south-bound,
      non-breaking). Mit dem Toggle wird jede unbestückte Methode hinter Auth gelegt; `[TrameAuthorise]`
      prüft weiterhin Rolle/Auth; `[TrameAnonymous]` öffnet gezielt (Health, Ping).
- [ ] **`RateLimitPermitLimit > 0`** setzen (`TrameOptions`, Fixed-Window). Default `0` = aus.
      REST-Endpunkte + SignalR-Hub bekommen `RequireRateLimiting("trame")`.
- [ ] **`MaximumBatchSize > 0`** setzen (`TrameOptions`). Default `0` = unbegrenzt. Deckt
      Fan-Out-DoS über `/json/multi`, JSON-RPC-Batch, WS-multi.
- [ ] **`MaxDependencyPathLength` / `AllowRecursiveDescent`** prüfen. Defaults `256` / `true`
      sind für north-bound konservativ genug; `AllowRecursiveDescent = false`härtet weiter.
- [ ] **Production-Environment** (`ASPNETCORE_ENVIRONMENT=Production`). Nur Dev leakt
      Stack-Traces (`EnableDetailedErrors`) und bedient die DevUI-Bundles uneingeschränkt.
- [ ] **Discovery hinter Auth.** Mit `RequireAuthentication=true` automatisch; sonst
      `/api/trame/discovery` und `trame.discover` manuell schützen (Angriffsflächen-Orakel).
- [ ] **Kestrel-Caps** (`MaxRequestBodySize`, Connection-Limits) setzen — Framework-seitig
      sind nur die 1 MB-Body-Caps auf REST/WS aktiv (F6.*).
- [ ] **DevUI nicht north-bound ausliefern.** `MapTrameDeveloperUi` in Produktion weglassen
      oder hinter Auth stellen (F7.1).

---

## A. Implementierte Härtung (v1.0 → north-bound)

Die folgenden Fixes sind im Code. Alle sind **opt-in und non-breaking** (Defaults bleiben
south-bound-erhalten), ausser wo das Verhalten additiv ist.

### F1.1 / F1.2 — Auth-Postur: `RequireAuthentication` lebendig + `[TrameAnonymous]`

**Vector:** ohne Auth-Default kann ein untrusted Client jede unbestückte Methode aufrufen — die
`[TrameAuthorise]`-Bestückung war bisher freiwillig und per Methode.

**Fix:** `TrameOptions.RequireAuthentication` (default `false`) ist jetzt ein lebendiger Toggle,
geplumbt über `AddTrame` → `TrameInvoker.RequireAuthentication` → `ITrameCore.RequireAuthentication`
(eine Wahrheitsquelle: TrameOptions → Invoker → Interface → alle Transporte). Die
Entscheidung fällt **im Invoker** (`TrameInvoker.CheckAuthorisation`), nicht im Transport-Endpoint
— dadurch bleibt `[TrameAnonymous]` als per-Method-Opt-out intakt (ein Endpoint-weites
`RequireAuthorization` würde unauth schon vor dem Invoker blockieren und das Opt-out brechen).

Matrix (`RequireAuthentication=true`):

| Methode bestückt mit | unauth | auth (keine Rolle) | auth (richtige Rolle) |
|---|---|---|---|
| — (unbestückt) | **401** (deny) | 200 | 200 |
| `[TrameAuthorise]` | 401 | 200 | 200 |
| `[TrameAuthorise(Role="Admin")]` | 401 | 401 | 200 |
| `[TrameAnonymous]` | **200** (opt-out) | 200 | 200 |
| Klasse `[TrameAuthorise]` + Methode unbestückt | 401 | 200 | 200 |
| Klasse `[TrameAuthorise]` + Methode `[TrameAnonymous]` | **200** (Methode gewinnt) | 200 | 200 |

`[TrameAuthorise]` ist jetzt `AttributeTargets.Method | AttributeTargets.Class` (Klassen-Level
gilt als Default für alle Methoden; Methoden-Level gewinnt). `[TrameAnonymous]` ist neu in
`TrameCore.Attributes` (`AttributeTargets.Method`).

**Tests:** `TrameTests/Unit/Core/AuthPostureTests.cs` — volle Matrix + per-Request-Batch-Deny
(ein 401 im Batch blockiert nicht die anderen; `[TrameAnonymous]` bleibt erreichbar).

### F9.1 / F9.2 — Transport-Gate: WebSocket-Upgrade + SignalR-Hub hinter Auth

**Vector:** der Invoker-Gate greift pro Request. Auf WebSocket/SignalR entsteht aber eine
**Verbindung** — wenn die steht, hat der Client den Kanal. Eine pro-Method-Entscheidung im Invoker
kann die Connection nicht mehr zurücknehmen.

**Fix (Defense in Depth):**
- **WebSocket** (`TrameWebSocketMiddleware`): Upgrade wird **vor** `AcceptWebSocketAsync`
  abgewiesen (401), wenn `RequireAuthentication` und unauth. WS hat **kein** per-Method-Opt-out
  (`[TrameAnonymous]` wirkt nur im Invoker-Gate auf REST) — die Verbindung ist die
  Vertrauensgrenze. Authentifizierung muss upstream `HttpContext.User` belegt haben.
- **SignalR-Hub** (`TrameServer/TramePipelineExtensions.MapTrame`): der Hub-Endpoint bekommt
  `.RequireAuthorization()`, wenn `RequireAuthentication`. Verbindungs-Gate vor dem Hub.

### F7.3 — Discovery hinter Auth

**Vector:** `/api/trame/discovery` und die JSON-RPC-Capability `trame.discover` exponieren die
vollständige Controller-/Methoden-/Typ-Introspektion — ein Angriffsflächen-Orakel: ein Angreifer
lernt jeden erreichbaren Endpunkt, jeden Parameter-Typ, jede Beispiel-JSON, ohne einen Call zu
tätigen.

**Fix:** wenn `RequireAuthentication`, gate-t der REST `/discovery`-Endpoint (401) und der
JSON-RPC-Dispatcher `trame.discover` (Error `-32001`). `trame.capabilities` (statisches Manifest
ohne Typ-Introspektion) bleibt öffentlich.

### F4.1 — Batch-Cap (`MaximumBatchSize`)

**Vector:** ein Client schickt einen Batch mit N Requests. Der Server fan-out-et parallel
(`Task.WhenAll`) — N Controller-Scopes, N Parameter-Bindungen, N Delegate-Invocations. Grosses
N = CPU-/Memory-DoS ohne Auth-Gate (die Auth-Pre-Pass ist serial, also selbst O(N), aber der
Fan-Out danach ist es auch).

**Fix:** `TrameOptions.MaximumBatchSize` (default `0` = unbegrenzt, non-breaking). Gates:
- REST `/json/multi` → frühes `400`.
- JSON-RPC batch-array → `-32600` Invalid Request.
- WS multi-request → `400` error-frame.
- Invoker-Backstop (`InvokeDi(IEnumerable<TrameRequest>)`) wirft `InvalidOperationException`
  für direkte In-Process-Aufrufer.

**Tests:** `NorthBoundHardeningTests.cs` — Cap überschritten → wirft; Cap=0 → beliebig; am Limit → ok.

### F3.1 — JsonPath-Begrenzung (`MaxDependencyPathLength` / `AllowRecursiveDescent`)

**Vector:** `dependencyMapping`-JsonPaths sind **client-kontrolliert**. `DependencyResolver.ExtractValue`
wertet sie via JsonPath.Net gegen das Provider-Result. Ein sehr langer Pfad oder ein `$..`
(Recursive descent) über einem großen Result-Graph treibt Parse + Evaluate zu einem CPU-Stall
(rechenintensivster Pfad-Typ).

**Fix:** Validierung **vor** `JsonPath.Parse`:
- `MaxDependencyPathLength` (default `256`, `0` = unbegrenzt): Pfad länger als Limit → `ArgumentException`
  → Aufrufer-Log behandelt als „Alias ungesetzt" → Provider exposiert nichts → Dependent bekommt
  sauberes 400 (kein 500, kein Stall — wir parsen nie).
- `AllowRecursiveDescent` (default `true`, non-breaking): `false` verbietet `..`-Pfade konservativ
  (String-Check — ein legitimer JsonPath enthält `..` nur als Recursive-Descent-Operator).

**Tests:** `NorthBoundHardeningTests.cs` — zu langer Pfad / `$..` disabled → Dependent 400;
Default-Limits → legitimer Pfad ok.

### F5.1 / F5.2 — Rate-Limiting

**Vector:** Connection-/Request-Flood. REST-Endpunkte sind bereits opt-in rate-limit-fähig
(Policy `trame`, Fixed-Window).

**Fix:**
- REST (`TrameEndpointExtensions`): `RequireRateLimiting("trame")` auf die Route-Gruppe, wenn
  `RateLimitPermitLimit > 0`. Default `0` = aus (non-breaking).
- **SignalR-Hub** (`MapTrame`): `.RequireRateLimiting("trame")` wenn `RateLimitPermitLimit > 0`.
- **WebSocket-Upgrade:** Roadmap (s. unten F5.2-W).

**Tests:** bestehende `SignalRTransportTests` decken den unlimitierten Pfad; das Rate-Limit-Gate
ist eine Endpoint-Konvention, build-gesichert.

### Strukturell sicher (kein Fix nötig)

- **Kein Code-Injection via Expression-Trees.** `TrameInvoker.CompileInvocation` baut den
  Delegate **ausschließlich** aus server-kontrolliertem `MethodInfo` (Reflection über die
  registrierte Controller-Assembly). Client-Eingaben fließen als **Parameter-Werte** in den
  kompilierten Delegate, nie als Code. Es gibt keinen `Compile`/`Emit`-Pfad, der Client-Strings
  zu IL macht. Der Client wählt Controller/Method per **Namen** (Dictionary-Lookup
  `"{Controller}_{Method}"`), nicht per Pfad — kein Path-Traversal, keine Late-Binding in
  willkürliche Typen.
- **JSON-RPC reacht nur registrierte Controller.** `JsonRpcAdapter` übersetzt `method` →
  `Controller.Method` (Split am letzten Punkt) und läuft durch denselben Dictionary-Lookup. Ein
  unbekannter Name → Routing-404 → `-32601`, kein Invoke.
- **Batch / WS / JSON-RPC bypassen `[TrameAuthorise]` nicht.** Alle Pfade laufen durch
  `ResolveAndAuthorizeAsync` (den serialen Auth-Pre-Pass bzw. den Single-Call-Check). Kein
  Transport erreicht `ExecuteAuthorized`, bevor die Auth geprüft ist.
- **Kardinalitäts-Caps.** `MaxParameterArrayLength` (default 1000) und `MaxResultElementCount`
  (default 10000) decken Array-/Collection-Parameter und Stream-Materialisierung (seit v1).
- **Body-Caps.** REST: `RequestSizeLimitAttribute(1 MB)` auf der Route-Gruppe. WS:
  `MaxMessageSize = 1 MB` hardcap in der Middleware (`HandleConnectionAsync`).
- **Parameter-Bindung über `System.Text.Json`** — kein `JsonConvert.DeserializeObject` mit
  Type-Steuerung; Typen sind server-seitig aus der Method-Signatur fix. Kein
  polymorpher Deserialization-Gadget-Vektor (die bekannten System.Text.Json-Polyfill-Risiken
  setzen `JsonSerializer` mit client-gesteuertem `TypeNameHandling`, was Trame nicht nutzt).
- **`byte[]`-Parameter** kommen als rohe Bytes aus `TrameRequest.BinaryData`, nicht als
  JSON-String — kein Base64-Parse-Fehlervektor in der Bindung.

---

## B. Roadmap (Medium/Low — nicht in v1.0 implementiert)

Diese Befunde sind im Audit identifiziert, bewusst aber **nicht** gehärtet — sie sind Medium/Low,
und die Härtung wäre entweder non-trivial oder mit South-Bound-Ergonomie konfliktär. Sie sind
hier dokumentiert, damit ein North-Bound-Betreiber sie kennt und kompensieren kann (häufig
bereits durch Kestrel-/Reverse-Proxy-Konfiguration upstream).

### F2.1 — MaxDepth / String-Längen-Caps bei Parameter-Deserialisierung
`System.Text.Json` hat Default-Depth-Caps, aber String-Längen und verschachtelte Tiefe jenseits
typischer Defaults werden nicht explizit beschränkt. Ein adversarieller Request-Body mit
extrem tiefen/langen JSON-Strukturen treibt Deserialisierung in O(n×Tiefe). **Kompensation:**
Kestrel `MaxRequestBodySize` (bereits 1 MB via Trame) + typ-spezifische Validierung im Controller.
**Roadmap:** `TrameOptions.MaxJsonDepth` durchreichen an `JsonSerializerOptions.MaxDepth`.

### F2.2 — Re-Parse-Amplifikation (Single-Pass-Modell)
`TrameResponseJsonConverter` und der Single-Pass-Pfad materialisieren `Data` (`JsonElement`)
mehrfach (Discovery, Exposes-Extraktion, Wire-Serialize). Bei sehr großen Results multipliziert
sich der Parse-Aufwand. **Kompensation:** `MaxResultElementCount`-Cap (seit v1) begrenzt die
Result-Größe an der Quelle (Stream-Materialisierung). **Roadmap:** Single-Pass für den
Exposes-Pfad prüfen.

### F4.2 — O(N²)-Graph im `DependencyGraphBuilder`
Die topologische Sortierung (Kahn) ist O(V+E) pro Batch; die Gruppierung unabhängiger Requests
in Parallel-Batches kann bei pathologischen Dependency-Mustern degradieren. **Kompensation:**
`MaximumBatchSize` cap-t die Batch-Größe und damit V. **Roadmap:** Cap auf Graph-Größe oder
Redundanz-Check.

### F4.3 — Alias-Chain-Tiefe
Ein Provider exposiert → sein Dependent exposiert → … transitiv. Tiefe Ketten treiben
Serial-Execution-Tiefe (und mit F2.2 Re-Parse). **Kompensation:** `MaximumBatchSize` + die
Transitivitäts-Propagation (ein fehlschlagender Provider bricht die Kette sauber in der
nächsten Batch-Stufe). **Roadmap:** explizites `MaxAliasChainDepth`.

### F4.4 — REST-Per-Client-Parallelität
Ein einzelner Client kann N concurrent `/json`-Requests öffnen (jeder ein Scope). Kein
Per-Client-Parallelitäts-Cap. **Kompensation:** `RateLimitPermitLimit` (Fixed-Window) drosselt
die Rate; Reverse-Proxy (nginx `limit_conn`) drosselt Concurrent-Connections. **Roadmap:**
Per-Client-Concurrency-Limiter-Policy.

### F7.1 — Dev-Stack-Leak in Produktion
`EnableDetailedErrors` (default `Development`-gebunden) leakt Stack-Traces in `error.details`.
Die DevUI (`MapTrameDeveloperUi`) ist ein Dev-Tool und sollte nicht north-bound ausgeliefert
werden. **Kompensation:** Production-Environment (kein Stack-Leak) + DevUI weglassen/hinter Auth.
**Roadmap:** DevUI-Endpoint explizit an `IHostEnvironment.IsDevelopment()` binden.

### F7.2 — Business-Error-Redaktion
`TrameResults.*`-Messages sind **nicht** an `EnableDetailedErrors` gebunden (bewusst — Domain-
Fehlermeldungen sollen den Client erreichen). Ein Controller-Autor kann aber sensitiven
Kontext in die Message legen. **Kompensation:** Controller-Autor-Disziplin. **Roadmap:** optional
`RedactBusinessErrors`-Toggle (nur Code+generische Message nach aussen).

### F10.1 — Binary-Cap
`byte[]`-Parameter via `BinaryData` haben keinen expliziten Größen-Cap über die 1 MB-Body-Cap
hinaus. **Kompensation:** 1 MB-Body-Cap (REST) / 1 MB-WS-Message-Cap. **Roadmap:** separates
`MaxBinaryPayloadSize`.

### F12.1 — Streaming-Materialisierung
`IAsyncEnumerable<T>`-Returns werden in eine `List<T>` materialisiert und als JSON-Array
serialisiert (kein echtes Streaming on-the-wire). Ein langer Stream → große List → Memory.
**Kompensation:** `MaxResultElementCount` cap-t die Elementzahl an der Quelle. **Roadmap:**
echtes Streaming on-the-wire (NDJSON / chunked) für north-bound.

### F1.4 — Policy / Claims-Bewertung
`[TrameAuthorise(Role=…)]` ist ein einfacher Rollen-Check (`IsInRole`). Komplexere Policies
(Claim-Werte, Multi-Faktor) müssen im Controller-Code oder einem自定义 `ITrameInterceptor`
geprüft werden. **Roadmap:** `[TrameAuthorize(Policy=…)]` an ASP.NET Core-Auth-Policies.

### F6.* — Kestrel-Caps
Trame setzt Framework-seitige 1 MB-Caps (REST-Body, WS-Message). Kestrel-Global-Caps
(`MaxRequestBodySize`-Default 30 MB, `MaxConcurrentConnections`, `MaxConcurrentUpgradedConnections`)
sind **Host-Verantwortung**, nicht Framework. **Kompensation:** in `Program.cs`
`builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxConcurrentConnections = …; })` setzen.
**Roadmap:** `TrameOptions.KestrelCaps`-Surface für die häufigsten Limits.

### F7.4 — Activator-Kosten
Controller werden per `IServiceScopeFactory.CreateScope()` + DI-Activator pro Call erzeugt
(Parallel-safe by construction). Für sehr hohe North-Bound-Last ist das ein Overhead-Faktor.
Kein Sicherheitsrisiko, aber ein Ressourcen-Faktor. **Roadmap:** optionale Controller-Factory-Cache.

### F8.2 — Notification-Flood (JSON-RPC / WS)
JSON-RPC-Notifications (ohne `id`) und WS-Messages ohne Response-Id emitieren keine Antwort —
ein Client kann fire-and-forget-Fluten. **Kompensation:** `RateLimitPermitLimit` + WS-MaxMessageSize.
**Roadmap:** Per-Client-Message-Rate-Limiter im WS-Transport.

### F9.3 — CORS
Trame setzt keine CORS-Policy. Für Browser-Clients north-bound muss CORS konfiguriert werden
(sonst Cross-Origin blockiert bzw. bei Wildcard `*` zu offen). **Kompensation:** Host
`app.UseCors("…")` mit benannter Policy. **Roadmap:** `TrameOptions.CorsPolicy`-Surface.

### F5.2-W — WebSocket-Upgrade Connection-Rate-Limit
Der WS-Transport ist `app.Map`-Branch-Middleware — kein Endpoint, also keine
`RequireRateLimiting`-Konvention. Die 1 MB-Message-Cap + das RequireAuthentication-Upgrade-Gate
entfernen den unauth-Flood-Vektor, aber ein authentifizierter Client kann Connections öffnen.
**Kompensation:** Reverse-Proxy-Connection-Limiting (nginx `limit_conn`) upstream.
**Roadmap:** WS-Transport auf Endpoint-Routing umstellen (dann `.RequireRateLimiting` wie
SignalR), oder in-Middleware Connection-Concurrency-Semaphore.

---

## C. North-Bound-Deployment-Checkliste (kompakt)

1. Auth-Schema konfigurieren (JWT/Cookie/mTLS), das `HttpContext.User` belegt.
2. `TrameOptions`: `RequireAuthentication = true`, `RateLimitPermitLimit > 0`, `MaximumBatchSize > 0`.
3. `ASPNETCORE_ENVIRONMENT = Production` (kein Dev-Stack-Leak).
4. DevUI nicht ausliefern (`MapTrameDeveloperUi` weglassen oder hinter Auth).
5. Kestrel-Limits setzen (`MaxConcurrentConnections`, `MaxRequestBodySize`).
6. Reverse-Proxy davor: TLS-Termination, Connection-Rate-Limit (für WS), Header-Filter.
7. Discovery ist hinter Auth (automatisch mit `RequireAuthentication`).
8. Smoke-Test: unauth Call → abgelehnt (native REST ist envelope-at-200: HTTP 200 mit body
   `"code":401`; die Framework-Gates Discovery/Batch-Cap/WS-Upgrade liefern echte HTTP 401/400);
   auth → 200; `[TrameAnonymous]`-Methode → 200 unauth; `[TrameAuthorise("admin")]` ohne Rolle →
   401; Batch > `MaximumBatchSize` → HTTP 400.

---

## D. Meldeprozess

Sicherheitsrelevante Schwachstellen bitte **nicht** als öffentliches Issue, sondern vertraulich
an den Maintainer. Gib Repro-Schritte, betroffenen Transport und — falls vorhanden — den
Audit-Befund-Code (F1–F12) an.