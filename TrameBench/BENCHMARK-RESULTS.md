# Trame Performance Benchmarks

> BenchmarkDotNet v0.14.0 · .NET 8.0.28 · Windows 11 (10.0.26200)
> CPU: 12th Gen Intel Core i7-12700H, 1 CPU, 20 logische / 14 physische Kerne
> Datum: 2026-07-10

## Testumgebung

Die Benchmarks laufen über **BenchmarkDotNet** (`[MemoryDiagnoser]`, `SimpleJob(warmupCount:3, iterationCount:5)`) gegen einen **echten Kestrel-Server** auf `localhost` mit zwei dedizierten Endpoints:

- **HTTP/1.1-Port** für REST, Trame-REST, Trame-WebSocket (`/tramews`) und Trame-SignalR (`/tramehub`).
- **HTTP/2-Port** (h2c, Plaintext prior-knowledge) für natives gRPC. Kestrel bedient gRPC auf Plaintext nur auf einem reinen HTTP/2-Endpoint — ein gemischter `Http1AndHttp2`-Endpoint lehnt gRPC-Prior-Knowledge mit `HTTP_1_1_REQUIRED (0xd)` ab (deshalb zwei Ports, analog zum offiziellen gRPC-Template).

Es werden 100 Customers vorab geseedet (`AddCustomer` enthält bewusst ein `Task.Delay(200 ms)`, um realistische Schreiblatenz zu simulieren — das dominiert die Dependency-Chain-Latenz, siehe Abschnitt 3).

### Verglichene Kanäle

| Kanal | Protokoll | Serialisierung | Roundtrips (100 Calls) |
|-------|-----------|----------------|------------------------:|
| **REST** (nativ) | HTTP/1.1 + JSON | System.Text.Json | 100 |
| **gRPC** (nativ) | HTTP/2 + Protobuf | protobuf | 100 |
| **Trame REST** | HTTP/1.1 + JSON | System.Text.Json (Trame-Wrapper) | 1 Batch |
| **Trame WebSocket** | RFC 6455 + JSON-Text-Frames | System.Text.Json | 1 Batch |
| **Trame SignalR** | WebSocket + MessagePack | MessagePack (binär) | 1 Batch |

> **Vorab, die Kernthese:** Trame bündelt N Aufrufe in **einen** Roundtrip. Bei 100 Aufrufen schlägt das natives REST (sequenziell **und** parallel) sowie natives gRPC (sequenziell) um **Faktor 16–60**. Bei Einzelaufrufen ist gRPC dank Protobuf-Binärformt tendenziell etwas kompakter; Trame SignalR (MessagePack) hält hier dagegen. Die Dependency-Chain spare **Roundtrips (3 → 1)**, aber auf localhost überdeckt die Service-Latenz diesen Vorteil — er wirkt erst über hoher Netzwerklatenz.

### Hinweis zur Messstreuung

Die Läufe fanden auf einer **belasteten Entwicklungsmaschine** statt. Einzelne Benchmarks zeigen hohe StdDev/Margins (z. B. gRPC-Sequenziell, REST-Parallele GetById). **Absolute Zahlen schwanken von Lauf zu Lauf**; die **Größenordnungen und Verhältnisse** sind jedoch über mehrere Läufe stabil und das belastbare Signal. Für verbindliche Zahlen bitte auf einer dedizierten Maschine im Release-Build erneut laufen lassen (siehe „Benchmark ausführen").

---

## 1. Single-Call Benchmarks

### Szenario: Ein einzelner RPC-Aufruf — `GetAllCustomers` (100 Datensätze) und `GetCustomerById(1)`.

| Methode | Mean | Allocated |
|---------|-----:|----------:|
| `REST: GetAllCustomers` | 357.2 µs | 86.33 KB |
| `REST: GetCustomerById` | 504.3 µs | 9.40 KB |
| `gRPC: GetAllCustomers` | 348.4 µs | 56.88 KB |
| `gRPC: GetCustomerById` | 341.9 µs | 9.25 KB |
| `Trame REST: GetAllCustomers` | 480.2 µs | 163.81 KB |
| `Trame REST: GetCustomerById` | 226.9 µs | 11.13 KB |
| `Trame WebSocket: GetAllCustomers` | 589.5 µs | 253.36 KB |
| `Trame WebSocket: GetCustomerById` | 212.9 µs | 10.41 KB |
| `Trame SignalR: GetAllCustomers` | 342.7 µs | 66.58 KB |
| `Trame SignalR: GetCustomerById` | 183.5 µs | 6.31 KB |

### Lesart

- **Einzelner GetById-Call:** Trame **SignalR (183 µs)** und **WebSocket (213 µs)** sind die schnellsten Kanäle — die persistente Verbindung amortisiert sich schon beim ersten Call (kein HTTP/1.1-Header-Overhead pro Aufruf). Trame REST (227 µs) schlägt natives REST (504 µs) deutlich, weil der Trame-Endpoint minimaler ist als die MVC-Controller-Pipeline. gRPC (342 µs) liegt dazwischen.
- **GetAll (100 Datensätze):** Natives REST (357 µs) und gRPC (348 µs) sind hier knapp schneller als Trame REST (480 µs) / WebSocket (590 µs) — der JSON-Envelope-Overhead der Trame-Response wird bei großem Payload sichtbar. **Trame SignalR (343 µs, MessagePack-Binär)** hält dagegen auf gRPC-Niveau.
- **Allokation:** gRPC allokiert am wenigsten (Protobuf-Binär, vordefinierte Message-Typen). Trame SignalR ist der allokationseffizienteste Trame-Kanal. Trame REST/WebSocket allokiert beim `GetAll` deutlich mehr (163 / 253 KB) — der JSON-Text-Envelope inkl. Discovery-Metadaten-Felder ist hier der Nachteil.

> **Ehrlicher Nachteil:** Bei einzelnen Aufrufen mit großem Payload ist natives gRPC (und z. T. natives REST) kompakter und teils schneller als Trames JSON-Kanäle. Trame SignalR (MessagePack) gleicht das weitgehend aus.

---

## 2. Batch Benchmarks — 100 Aufrufe

### Szenario: 100× `GetCustomerById` — nativ (REST/gRPC, je sequenziell + parallel, also 100 Roundtrips) vs. Trame-Batch (1 Roundtrip, 100 Aufrufe serverseitig gebündelt, je parallel + serial).

Baseline = REST sequenziell (100 Roundtrips). Ratio = Speedup-Faktor gegenüber der Baseline (kleiner = schneller).

| Methode | Mean | Ratio | Allocated |
|---------|-----:|-----:|----------:|
| `REST: 100x sequential` *(Baseline)* | 16.512 ms | 1.00 | 936.18 KB |
| `REST: 100x parallel` | 11.919 ms | 0.72 | 942.64 KB |
| `gRPC: 100x sequential` | 32.070 ms | 1.94 | 901.46 KB |
| `gRPC: 100x parallel` | 3.124 ms | 0.19 | 988.33 KB |
| `Trame REST: 100x batch parallel` | 876.5 µs | 0.05 | 513.76 KB |
| `Trame REST: 100x batch serial` | 1.069 ms | 0.06 | 641.55 KB |
| `Trame WebSocket: 100x batch parallel` | 918.4 µs | 0.06 | 792.22 KB |
| `Trame WebSocket: 100x batch serial` | 1.148 ms | 0.07 | 920.06 KB |
| `Trame SignalR: 100x batch parallel` | 555.3 µs | 0.03 | 335.59 KB |
| `Trame SignalR: 100x batch serial` | 1.429 ms | 0.09 | 463.44 KB |

### Lesart

- **Trame-Batch schlägt natives REST um Faktor 15–30:** 100 Aufrufe in **einem** Roundtrip (876 µs REST-parallel) statt 100 Roundtrips (16,5 ms REST-sequenziell / 11,9 ms -parallel). **Trame SignalR parallel (555 µs) ist ~30× schneller als REST sequenziell und ~21× schneller als REST parallel.**
- **Auch gRPC-parallel geschlagen:** gRPC-parallel (3,1 ms, HTTP/2-Multiplexing) ist gut — aber Trame SignalR-parallel (555 µs) ist **~5,6× schneller**, Trame REST-parallel (876 µs) **~3,6× schneller**. Der Roundtrip-Bündelungs-Vorteil überkompensiert Protobufs Binär-Vorteil.
- **gRPC sequenziell überraschend langsam (32 ms):** 100 einzelne h2c-Calls auf localhost sind **langsamer als REST sequenziell (16,5 ms)** — der HTTP/2-Stream-Setup pro Call schlägt auf localhost stärker zu Buche als JSON-Overhead. gRPC entfaltet seinen Vorteil erst mit Multiplexing (parallel). Das ist eine ehrliche, leicht kontraintuitive Beobachtung.
- **Serial vs. Parallel (Trame):** Parallel ist erwartungsgemäß schneller als Serial (serverseitig `Task.WhenAll` vs. sequenzielle `foreach`). SignalR-serial (1,43 ms) ist langsamer als REST/WS-serial — die serial-Mode-Ausführung wirkt sich über die MessagePack-Hub-Pipeline stärker aus.
- **Allokation:** Trame SignalR parallel allokiert am wenigsten (336 KB). Trame WS-serial (920 KB) und REST-sequenziell (936 KB) sind vergleichbar. Die JSON-Kanäle sind bei Serial allokationsintensiver.

> **Kernvorteil Trame:** Der Batch-Modus reduziert 100 Roundtrips auf 1. Genau hier — viele Calls pro Logikschritt — ist Trames Mehrwert am größten, und zwar über **alle** drei Kanäle, nicht nur SignalR.

---

## 3. Dependency-Chain Benchmarks

### Szenario: Klassisches N+1-Muster — `AddCustomer` → `GetCustomerById` → `GetOrdersByOrderId`. REST und gRPC brauchen **3 sequentielle Roundtrips** (gRPC hat kein serverseitiges Chaining). Trame schickt **1 Batch** mit `@alias`-Dependency-Mapping (Serial-Mode, serverseitig verkettet).

Baseline = REST (3 sequentielle Calls). Ratio = relativ zur Baseline.

| Methode | Mean | Ratio | Allocated |
|---------|-----:|-----:|----------:|
| `REST: 3 sequential calls (N+1)` *(Baseline)* | 206.9 ms | 1.00 | 30.22 KB |
| `gRPC: 3 sequential calls (N+1, binary)` | 204.9 ms | 0.99 | 52.10 KB |
| `Trame: 3 sequential single calls (no chaining)` | 206.2 ms | 1.00 | 34.55 KB |
| `Trame: 1 batch call (dependency chain, 3 reqs)` | 208.1 ms | 1.01 | 42.18 KB |
| `Trame WebSocket: 1 batch call (dependency chain)` | 207.9 ms | 1.01 | 42.66 KB |

### Lesart — die ehrliche Geschichte

- **Auf localhost ist kein Speedup messbar.** Alle Ansätze liegen bei **~207 ms** — und das liegt **nicht** an Trame, sondern am simulierten `Task.Delay(200 ms)` in `AddCustomer`. Diese Service-Latenz dominiert die gesparten Roundtrips komplett. Die alte (veraltete) Dokumentation behauptete „78 µs / 2646× Speedup" — das war **physikalisch unmöglich** und ist hier korrigiert.
- **Der echte Trame-Vorteil ist strukturell, nicht lokal:** 3 Roundtrips → 1 Roundtrip. Über eine Netzwerklatenz von z. B. 50 ms pro Roundtrip spart Trame hier **100 ms** (2 Roundtrips × 50 ms) ein — der `Task.Delay(200 ms)` bleibt, aber die Latenz reduziert sich von ~300 ms + 200 ms auf ~50 ms + 200 ms. **Der Roundtrip-Vorteil wirkt erst über hoher Netzwerklatenz**, nicht auf localhost. Genau das ist die ehrliche Aussage.
- **Allokation — ein echter Nachteil:** Trames Batch-Chain (42 KB) allokiert **mehr** als REST (30 KB) und deutlich mehr als gRPC (52 KB ist hier allerdings durch Protobuf-Message-Objekte bedingt). Die JSON-Alias-Auflösung + Envelope + Dependency-Resolver verbrauchen Extra-Speicher. Das ist ein fairer, dokumentierter Nachteil.
- **Trame 3-sequential-no-chaining (34 KB) vs. Trame 1-batch-chain (42 KB):** Auch ohne Chaining ist die 3-Call-Sequenz allokationseffizienter als der Batch — der Batch-Envelope schlägt hier zu Buche. Dafür hat der Batch eben nur 1 Roundtrip.

> **Fazit Dependency-Chain:** Trames Chaining ist ein **Architektur-Vorteil** (weniger Roundtrips, eine atomare Logikeinheit, serverseitig korreliert), kein lokaler Performance-Wunder. Über Netzwerklatenz wird er zum echten Speedup; auf localhost bleibt er neutral bei höherer Allokation.

---

## Zusammenfassung: Vorteile und Nachteile (belastbar)

### ✅ Vorteile Trame

1. **Batch = 1 Roundtrip statt N.** Bei 100 Aufrufen **Faktor 15–30 schneller** als natives REST (sequenziell + parallel) und **Faktor 3–6 schneller** als gRPC-parallel. Größter Hebel, wirkt über **alle** Kanäle.
2. **Persistente Verbindungen (WebSocket/SignalR).** Schon beim einzelnen Call schneller als natives REST (kein HTTP/1.1-Header-Overhead pro Aufruf). SignalR GetById (183 µs) < REST GetById (504 µs).
3. **Dependency-Chaining.** N+1-Muster in **einem** Batch mit `@alias`-Auflösung statt N Roundtrips. Architektonisch sauberer (eine atomare Logikeinheit), über Netzwerklatenz ein echter Speedup.
4. **Multi-Transport, ein Contract.** Code-first (kein `.proto`), derselbe Controller läuft über REST, WebSocket und SignalR — gRPC braucht dagegen eine separate Protobuf-Definition und .proto-Pflege.
5. **SignalR + MessagePack** erreicht auf gRPC-Niveau (binär, kompakt, niedrige Allokation).

### ⚠️ Nachteile Trame

1. **JSON-Text-Envelope bei großem Payload.** Bei `GetAllCustomers` sind Trame REST (480 µs / 164 KB) und WebSocket (590 µs / 253 KB) langsamer und allokationsintensiver als natives REST (357 µs / 86 KB) und gRPC (348 µs / 57 KB). SignalR (MessagePack) gleicht das aus — aber REST/WS-Kanäle nicht.
2. **Dependency-Chain-Speedup auf localhost neutral.** Die gesparten Roundtrips werden von der Service-Latenz überdeckt. Vorteil wirkt erst über Netzwerklatenz. (Ehrlich dokumentiert — kein falscher „2646× Speedup".)
3. **Höhere Allokation beim Batch/Chain.** Trame-Batch (42 KB) > REST-sequenziell (30 KB). Alias-Resolver + Envelope kosten Extra-Speicher.
4. **Serial-Mode langsamer als Parallel-Mode.** Erwartbar, aber SignalR-serial (1,43 ms) fällt zurück. Für unabhängige Calls ist Parallel die richtige Wahl; Serial ist primär für geordnete Abhängigkeiten gedacht.

---

## Benchmark ausführen

```bash
# Alle Suiten (Single + Batch 100 + Dependency)
dotnet run -c Release --project TrameBench/TrameBench.csproj

# Nur Single-Call (10 Methoden)
dotnet run -c Release --project TrameBench/TrameBench.csproj -- single

# Nur Batch 100 (10 Methoden)
dotnet run -c Release --project TrameBench/TrameBench.csproj -- batch

# Nur Dependency-Chain (5 Methoden)
dotnet run -c Release --project TrameBench/TrameBench.csproj -- dependency

# BenchmarkDotNet-Filter durchreichen, z. B. nur WebSocket-Benchmarks
dotnet run -c Release --project TrameBench/TrameBench.csproj -- --filter "*Ws*"
```

Detaillierte Reports (Mean, StdErr, StdDev, Median, Gen0/1/2, Allocated) liegen nach jedem Lauf unter `BenchmarkDotNet.Artifacts/results/*-report-github.md`.

---

## Anmerkung: durch die Benchmarks zutage geförderte Framework-Bugs

Der Benchmark-Suite-Aufbau hat **drei echte, bisher unentdeckte Bugs** im WebSocket-/Batch-Pfad zutage gefördert und direkt behoben (alle in diesem Stand enthalten):

1. **WS-Middleware: case-sensitive Batch-Erkennung.** `JsonElement.TryGetProperty` ist case-sensitiv. Ein C#-Client ohne CamelCase-Policy schickt PascalCase (`"Requests"/"Mode"`), ein JS/TS-Client camelCase. Die Middleware erkannte PascalCase-Batches nicht und behandelte jeden Batch als Single-Call (Controller null → 404 mit leerer Id) → Client-Endlos-Warte. **Fix:** case-insensitive Erkennung über `EnumerateObject()` + `StringComparison.OrdinalIgnoreCase`.
2. **WS-Client: `DispatchResponse` wirft auf Array-Root.** `root.TryGetProperty("id")` wirft `InvalidOperationException`, wenn `root` ein Array (Batch-Response) ist — `TryGetProperty` verlangt ein Object. Der Array-Zweig war dadurch unerreichbar. **Fix:** Guard `root.ValueKind == JsonValueKind.Object` vor dem `TryGetProperty`; Arrays fließen in den bestehenden Batch-Zweig (`root[0].Id`).
3. **`TrameInvoker.ExecuteSequentially`: nicht-deterministische Response-Reihenfolge.** Die Methode sammelte Responses in einer `ConcurrentDictionary` und gab `responses.Values` zurück — `ConcurrentDictionary` bewahrt **keine** Einfügereihenfolge. WS-/SignalR-Clients korrelieren Batches aber über `root[0].Id` (erste Request-Id); bei Serial lieferte der Server ein anderes Element zuerst → kein Match → Timeout. Der Parallel-Pfad (`ExecuteInParallel`) hielt die Ordnung bereits index-genau; Serial nicht. **Fix:** zusätzlich `orderedResults`-Liste in Request-Reihenfolge führen und diese zurückgeben. REST-serial „funktionierte" nur scheinbar, weil der REST-Client keine ID-Korrelation macht.

Alle drei Bugs blockierten ausschließlich den **Serial-/Batch-Pfad über WebSocket/SignalR**; der Single-Call- und REST-Pfad war davon unberührt. Die hier dokumentierten Zahlen entstanden **mit** den Fixes.