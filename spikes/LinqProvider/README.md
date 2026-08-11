# LINQ-Provider-Spike (Prototyp)

> **Spike / Prototyp — kein Produktionscode.** Demonstriert, wie sich Sleipnir-Calls
> typsicher aus C#-Code heraus konstruieren lassen, sodass Controller, Methode,
> Parametertypen und vor allem die `@alias`/JsonPath-Verdrahtung eines Batches
> vom Compiler geprüft werden statt zur Laufzeit aus JSON-Strings zusammengebaut.

## Idee

Im Status quo baut der Client einen Batch aus handgeschriebenen `SleipnirRequest`-JSON:
`dependencyMapping: { "newId": "$" }` hier, `data: "@newId"` dort — beides untypisierte
Strings, die der Compiler nicht prüft. Ein Tippfehler im Alias oder ein falscher
JsonPath fällt erst zur Laufzeit (als 500) auf.

Der Spike kehrt das um: **die C#-Verträge sind der Contract.** Ein LINQ-Provider-artiger
Client nimmt ein Lambda `c => c.GetCustomerById(newId)`, übersetzt den
`MethodCallExpression` in einen `SleipnirRequest` (Controller/Methode aus Attributen,
Parameter aus der Signatur) und erkennt `Dep<T>`-Platzhalter, die dann zu `@alias`
werden. Die Verdrahtung entsteht typsicher:

```csharp
var client = new SleipnirLinqClient(restClient);

// Ein typsicherer Call:
int newId = await client.SendAsync(
    client.Build((ICustomerService c) => c.AddCustomer("Alice")));

// Batch mit Dependency-Chaining — der Compiler prüft, dass newId ein int ist:
var create = client.Build((ICustomerService c) => c.AddCustomer("Bob"));
Dep<int> id = create.Expose();                       // ganzes Resultat ($) → int
var fetch  = client.Build((ICustomerService c) => c.GetCustomerById(id));

var responses = await client.SendAsync(new SleipnirBatch(create, fetch));
Customer bob = client.ResultOf<Customer>(fetch, responses);  // typisierte Extraktion
```

Ein `Dep<string>` an einer `Arg<int>`-Stelle ist ein **Compile-Fehler**, kein Laufzeit-500.

## Entscheidung (Stand 2026-07-08)

Der Spike **beweist, dass der Weg technisch gangbar ist** — er hat die drei
Server-Bugs im Dependency-Chaining überhaupt erst sichtbar gemacht, weil die
getypte Verdrahtung einen numerischen Chain end-to-end ausführt. Aber er wird
**nicht in v1 ausgebaut**, sondern als **v1.x-Opt-in** mit eingeschränktem Scope
angestrebt (siehe `ROADMAP.md` „v1.1 — Versionierung & Build-Time-Vertrag").

### Ehrliche Einordnung

- **Kein echter LINQ-Provider.** Sleipnir braucht keine Query-Tree-Übersetzung
  (kein `IQueryable`, kein Where/Select/OrderBy serverseitig) — nur Methoden-
  aufrufe + Parameter-Verdrahtung. Der Spike ist ein **Expression-Tree-getypter
  RPC-Proxy**. Das ist *gute* Nachricht: das schwierige IQueryable-Monad-Problem
  entfällt. Der Name „LINQ-Provider" ist aspirativ; „getypter Proxy" trifft es.
- **ROI begrenzt.** Der untypisierte `SleipnirCall`-Builder + `WithAlias` funktioniert
  heute. Die getypte Schicht bringt Compile-Zeit-Sicherheit für Controller/
  Methode/Parametertyp/Dep-Typ. Davon fallen Methoden-/Parameterfehler ohnehin
  in der Entwicklung sofort zur Laufzeit auf; der **größte** Gewinn (typisierte
  Dep-Verdrahtung) deckt zugleich den **engsten** Fall (Batch-Dependency-Chains).
  Den Codegen + `Arg<T>`-Tax rechtfertigt das nur als opt-in, nicht als Default.

### Für ein v1.x-Produkt empfohlener Scope (falls überhaupt)

- Typisierte Einzel-Calls + typisierte Batch-Chaining.
- `Arg<T>` **nur** an Parameterpositionen, die Deps empfangen — nicht flächendeckend.
- Codegen als `dotnet tool` / Source-Generator gegen einen **committed
  Discovery-Snapshot** (nicht gegen einen zur Build-Zeit laufenden Server), plus
  **Drift-Check** serverseitig (sonst Vertrag ≠ Server — die wsdl-Falle).
- Generator baut intern weiter auf `SleipnirCall`/`ISleipnirClient` — getypter Wrapper,
  kein zweites Protokoll (wie in ROADMAP vorgesehen).

### Aus dem Spike gelernte, für ein v1.x zu lösende Punkte

- **Codegen ist nicht ins Build integriert** — `Contracts.cs` ist handgeführt;
  `ContractGenerator` spuckt nur einen String. Source-Generator / `dotnet tool`
  muss `Contracts.g.cs` wirklich erzeugen.
- **Vertrag ≠ Server** — stimmt der committed Snapshot nicht mehr mit dem Server
  überein, compiliert der Client sauber und scheitert erst zur Laufzeit. Der
  Drift-Check ist der Pflichtbestandteil, nicht Nice-to-have.
- **Reflection-Kosten pro Call** — `Expression.Lambda(arg).Compile().DynamicInvoke()`
  je Argument. Ein echter Provider faltet Konstanten zur Bauzeit und cacht.
- **Server-Constraint leckt ins Codegen** — Aliase müssen `[A-Za-z0-9_]` sein,
  weil `DependencyGraphBuilder.ExtractAliases` an `.`/`#` abbricht (siehe
  `SleipnirCallSpec.ExposePath`). Sauberer wäre, den Server Aliase freier extrahieren
  zu lassen — ein Server-Change, kein Client-Workaround.
- **Offen für v1.x**: Cancellation im Lambda (bricht das reine Expression-Modell),
  `IAsyncEnumerable`-Streaming, `byte[]`-Binary, void-Return — der untypisierte
  `SleipnirRestJsonClient` kann das schon, die getypte Schicht müsste es durchreichen.

## Komponenten

| Datei | Rolle |
|-------|-------|
| `ContractAttributes.cs` | `[SleipnirServiceContract]`/`[SleipnirMethodContract]` — Metadaten auf generierten Verträgen |
| `Contracts.cs` | Repräsentativer Generator-Output (`ICustomerService`) gegen den Customer-Controller |
| `ContractGenerator.cs` | `discovery.json → C#` (das „discovery-generiert"-Modell, Option c) |
| `Arg.cs` | `Arg<T>`-Wrapper + implizite Konvertierungen aus `T` und `Dep<T>`; `IArg` als nicht-generische Sicht |
| `Dep.cs` / `SleipnirCallSpec.cs` | `Dep<T>`-Marker; `SleipnirCallSpec<T>.Expose()`/`Expose(x => x.Name)` |
| `JsonPathBuilder.cs` | Selector-Expression → ergebnisrelativer JsonPath (`$`, `$.Name`, `$[0].Id`) |
| `SleipnirLinqClient.cs` | Expression-Visitor → `SleipnirCallSpec`; Send/ResultOf |
| `SleipnirBatch.cs` | sammelt Specs → `SleipnirMultiRequest` |
| `SleipnirLinqClientTests.cs` | In-memory-xUnit gegen `WebApplicationFactory<Program>` (reparierte Chaining-Basis) |

## Verifizierung

```bash
dotnet test spikes/LinqProvider/Sleipnir.Spike.LinqProvider.csproj   # 5/5 grün
```

Die Integrationstests laufen in-memory gegen die Sample-App und decken:
- einen typisierten Einzel-Call,
- einen Batch mit numerischem `Dep<int>` (`AddCustomer → id → GetCustomerById`),
- einen 3-Stufen-Batch mit String-`Dep` über `$.Name` (`AddCustomer → GetById → $.Name → AddCustomer`),
- den `ContractGenerator` (Discovery → C#) und
- den `JsonPathBuilder` (`Expose`-Pfade).

## Baut auf der reparierten Dependency-Chaining-Basis

Der Spike funktioniert nur, weil die Server-Seite vorher repariert wurde:
- **Bug #1** Konvention: JsonPath ist ergebnisrelativ (`$`/`$.Prop`/`$[0].Id`), kein `$.data`.
- **Bug #2** typgetreue `@alias`-Substitution (`extracted.ToJsonString()` + `JsonValue.Create`).
- **Bug #3** der topologische Batch-Pfad (`ExecuteInDependencyBatches`) löst `@alias`
  gegen vorherige Responses auf — zuvor passierte das nur im Serial-Pfad.

Ohne Bug #3 würde jeder Batch mit `dependencyMapping` (also jeder mit `Dep<T>`)
einen 500 liefern — genau der Pfad, den der Spike abdeckt.

## Grenzen / nächste Schritte (bewusst out-of-scope für den Prototyp)

- **Generator ist nicht ins Build integriert** — `Contracts.cs` ist handgeführt; ein
  echter Source-Generator / `dotnet tool` würde `Contracts.g.cs` aus der laufenden
  Discovery schreiben.
- **Argumente werden pro-Call kompiliert** (`Expression.Lambda(arg).Compile()`) —
  korrekt, aber nicht optimal. Ein echter LINQ-Provider klappt konstante Teile zur
  Bauzeit ein.
- **Kein Cancel­lation/Streaming/byte[]** im Spike — Fokus auf die Verdrahtung.
- **Alias-Schema** ist `_`-sanitisiert, weil der serverseitige `DependencyGraphBuilder`
  `@alias` nur als `[A-Za-z0-9_]+` extrahiert (siehe `SleipnirCallSpec.ExposePath`).