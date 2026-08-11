# Phase 1 — Interceptor-Pipeline Design

> Roadmap: `ROADMAP.md` → Benutzbarkeit-Roadmap → Phase 1 (gekoppelter Durchgang).
> Status: **entworfen**, Entscheidungen getroffen am 2026-08-07. Noch nicht implementiert.
>
> Phase 1 baut **ein Seam** für drei heute spezialfallige Belange: Autorisierung (1),
> Telemetrie (4) und Fehler-Taxonomie (A). Ein Architektur-Entscheid, nicht drei.

---

## Bestand (Fakten)

Erhoben via Subagent gegen `SleipnirCore/Services/SleipnirInvoker.cs` und Drumherum. Die drei
Speziallocken:

- **Autorisierung** — `CheckAuthorisation` (`SleipnirInvoker.cs` Z. 1818–1845) +
  `[SleipnirAuthorise].OnAuthorization` (`SleipnirCore/Attributes/SleipnirAuthoriseAttribute.cs` Z. 56–84,
  nur `IsInRole(string)`). Wirft `UnauthorizedAccessException`, vom Aufrufer in 401 übersetzt. Drei
  Aufrufsorte: Single (`ExecuteSingleInvocationSimple` Z. 315), Batch-Pre-Pass
  (`ResolveAndAuthorizeAsync` Z. 1146, serial vor Fan-out), Serial (`ExecuteSequentially` Z. 388).
- **Telemetrie** — `SleipnirCore/Tracing/SleipnirTracing.cs` (`ActivitySource "Sleipnir"`), aufgerufen an
  acht Stellen im Invoker (`StartCall`/`SetCallStatus`/`RecordException`/`StartBatch`). Fest
  verdrahtet, keine Schnittstelle. Nur Traces, keine Metrics, keine Logging-Conventions.
- **Fehler** — zwei parallele Fabriken ohne gemeinsame Konstanten: `SleipnirResults`
  (`SleipnirCommon/Results/SleipnirResults.cs`, Controller-Seite) und private Fabriken im Invoker
  (`SleipnirInvoker.cs` Z. 1849–1898). Magic Numbers. `SleipnirError.RequestId` wird im Framework-Pfad
  nie belegt. JSON-RPC-Map (`JsonRpcAdapter.MapErrorCode` Z. 147) koppelt per String-Präfix an
  Invoker-Fehlermeldungen.

**Bestehende Interceptor-Infrastruktur (halb fertig):**
`ISleipnirInterceptor` (`SleipnirCore/Services/ISleipnirInterceptor.cs`) existiert, ist aber:
nur im Single-Call-Pfad angebunden (`InvokeDi(SleipnirRequest)` Z. 230–237); ohne `HttpContext` im
Delegate; mit ungenutztem `SleipnirInvocationContext`; ohne `SleipnirOptions.Interceptors`-Collection;
von Batch-Pfaden komplett umgangen. `SleipnirLoggingInterceptor` ist der einzige Built-in.

---

## Architektur: zwei-Pfad-Pipeline mit geteiltem Kern

Kein Versuch, Batch und Single über *einen* Pipeline-Typ zu zwingen — die Serial/Parallel-
Constraint (HttpContext ist nicht thread-safe, Auth muss serial vor Fan-out) macht das unehrlich.
Stattdessen zwei explizite Einstiegspfade, die denselben Interceptor-Kern nutzen.

### Einheitlicher Context

```csharp
public sealed class SleipnirInvocationContext
{
    public required SleipnirRequest Request { get; init; }
    public HttpContext? HttpContext { get; init; }
    public InvokeInfo? InvokeInfo { get; set; }   // nach Resolve, für Auth sichtbar
    public SleipnirResponse? Response { get; set; }  // nach Execution, für Tracing/Logging
    public Activity? Activity { get; set; }       // der SleipnirCall-Span
    public CancellationToken CancellationToken { get; init; }
}
```

Ersetzt das ungenutzte `SleipnirInvocationContext` und reicht `HttpContext` durch — die
Schlüssellücke heute.

### Interceptor-Typen

- `ISleipnirInterceptor` (bestehend, erweitert) — pro Request-Invocation, bekommt den Context.
- `ISleipnirBatchInterceptor` (neu) — pro Batch, läuft *um* die Batch-Ausführung herum (Metrics,
  Logging auf Batch-Ebene, Batch-Rate-Limiting).

### Interceptor-Reihenfolge (fest, dokumentiert)

```mermaid
graph LR
    A[1. RateLimit] --> B[2. Auth]
    B --> C[3. Validation]
    C --> D[4. Tracing/Metrics]
    D --> E[5. Method-Invocation]
    E --> F[6. Response-Post]
```

- **RateLimit zuerst** — lehnt ab, bevor irgendetwas teures läuft.
- **Auth vor Validation/Tracing** — sonst validierst/loggst du unautorisierten Traffic. Auth
  bleibt serial im Batch-Pre-Pass (HttpContext), passt weil RateLimit+Auth die serial-Phase sind.
- **Validation vor Tracing** — Tracing misst nur autorisierten, validierten Traffic.
- **Tracing umschließt Method-Invocation** — der `SleipnirCall`-Span bleibt, wie er ist; der
  Telemetry-Interceptor nutzt ihn aus dem Context statt neue Spans aufzumachen.

### Die drei Speziallocken werden zu Interceptors

**1. `SleipnirAuthorizationInterceptor` (Punkt 1 — Policies)**
- Übernimmt `CheckAuthorisation`-Logik + `[SleipnirAuthorise].OnAuthorization`.
- Erweitert `[SleipnirAuthorise]` um `Policy` (an ASP.NET Core `IAuthorizationService`).
- `[SleipnirAnonymous]` wird zur Skip-Annotation (Interceptor prüft `InvokeInfo.AnonymousAttribute`
  zuerst, short-circuitet).
- Default-Interceptor (DI), ersetzt die direkten `CheckAuthorisation`-Aufrufe.
- Im Batch-Pfad bleibt der serial Pre-Pass — ruft *denselben* Interceptor, nicht duplizierte Logik.

**2. `SleipnirTelemetryInterceptor` (Punkt 4 — OTel Metrics/Logging)**
- Übernimmt die acht `SleipnirTracing.*`-Aufrufe — eine Stelle.
- Erweitert um Metrics (`Meter "Sleipnir"`: `sleipnir.call.duration` Histogram,
  `sleipnir.call.count` Counter, `sleipnir.batch.fan_out`, `sleipnir.error.rate`).
- Logging-Conventions: strukturierte Felder passend zu OTel-RPC-Semantic-Conventions.
- `ISleipnirBatchInterceptor`-Variante für `sleipnir.batch.*`-Spans/Metrics.

**3. Fehler-Taxonomie (Punkt A) — eine Ebene tiefer, keine Interceptor-Klasse**
- `SleipnirErrorCodes` (neu) ersetzt Magic Numbers in `SleipnirResults` und Invoker-Fabriken.
- `SleipnirError.Category` (neu, optional) — semantische Kategorie (`InvalidArgument`/`NotFound`/
  `Unauthenticated`/`PermissionDenied`/`FailedPrecondition`/`Unavailable`/`ResourceExhausted`),
  *zusätzlich* zum numerischen `Code` (nicht ersetzend — `STABILITY.md` §1.4 hält das fest).
- `SleipnirResults` und Invoker-Fabriken werden *gemeinsam* auf `SleipnirErrorCodes` +
  `CreateError(code, category, message)` umgestellt — eine Factory, nicht zwei.
- JSON-RPC-Mapping nutzt `Category` statt String-Präfix (löst die implizite Kopplung in
  `JsonRpcAdapter.MapErrorCode`).
- Generierte Clients werfen typisierte Exceptions pro Kategorie (Codegen-Teil, folgt mit Phase 3-B).

### Die vier konkreten Code-Schritte

1. **`SleipnirOptions.Interceptors` + `SleipnirOptions.BatchInterceptors` Collections** + DI-Wiring.
   Default: die drei Built-ins (Auth/Telemetry/Logging) in fester Reihenfolge, User-Interceptors
   anhängbar.
2. **`SleipnirInvocationContext` erweitern** (s.o.), `ISleipnirInterceptor.InvokeAsync` darauf
   umstellen, `ISleipnirBatchInterceptor` neu.
3. **Batch-Pfad an die Pipeline anbinden:** `ExecuteInParallel`/`ExecuteSequentially`/
   `ExecuteInDependencyBatches` rufen pro Element die Interceptor-Pipeline statt
   `ExecuteAuthorized` direkt — *aber* der serial Auth-Pre-Pass bleibt, weil HttpContext serial
   sein muss. Die Pipeline läuft im parallelen Teil (nach dem Pre-Pass), der Auth-Interceptor im
   Pre-Pass ist derselbe Interceptor.
4. **Fehler-Fabriken vereinheitlichen:** `SleipnirErrorCodes` + `SleipnirError.Category`, `SleipnirResults`
   und Invoker-Fabriken umstellen, JSON-RPC-Mapping auf `Category` umstellen.

---

## Getroffene Entscheidungen (2026-08-07)

### Entscheidung 1 — `ISleipnirInterceptor`-Signatur-Break: **ja**

Heute `InvokeAsync(SleipnirRequest, SleipnirInvocationDelegate, CancellationToken)`. Umstellung auf
`InvokeAsync(SleipnirInvocationContext, Func<SleipnirInvocationContext, Task<SleipnirResponse?>>)`. Das ist
ein **breaking change** für jeden, der `ISleipnirInterceptor` heute implementiert (im Repo nur
`SleipnirLoggingInterceptor`, aber potenziell externe User). `STABILITY.md` §2 markiert
`ISleipnirInterceptor` als **experimental** — also ist der Break innerhalb 1.x erlaubt. Der
einzige Built-in (`SleipnirLoggingInterceptor`) wird mit umgestellt.

### Entscheidung 2 — `SleipnirError.Category` als additives Feld: **ja**

`SleipnirError.Category` (neu, optional) kommt *neu* hinzu. Das ist eine `SleipnirError`-Schema-
Erweiterung. `STABILITY.md` §3.2 "additive = minor" deckt das — ein neues Feld, bestehende
Clients ignorieren es. Einsortiert als additives Minor-Change, keine SemVer-Major.

### Entscheidung 3 — Policies via `IAuthorizationService`, `resource = null` in v1.1

`[SleipnirAuthorise(Policy = "CanApproveOrder")]` ruft
`IAuthorizationService.AuthorizeAsync(user, resource, policy)` auf. Da Sleipnir command-orientiert
ist und Controller-Methoden keinen klaren "resource"-Begriff haben: **(a) `resource = null` für
v1.1** (nur Policy-Check, kein resource-basierter). Ein `[SleipnirAuthorizeResource]`-Hook für
resource-basierte Policies ist **(c) v1.x+** — nicht Teil von Phase 1.

### Entscheidung 4 — Meter-Name `"Sleipnir"`

`Meter` und `ActivitySource` tragen denselben Namen `"Sleipnir"` (OTel-Konvention erlaubt das).
`SleipnirTracing.ActivitySourceName = "Sleipnir"` bleibt; neuer `SleipnirMetrics.MeterName = "Sleipnir"`.

---

## Abgrenzung (was Phase 1 *nicht* macht)

- **Kein Validation-Interceptor.** Die Pipeline-Platzierung ist da (Reihenfolge 3), aber das ist
  ein separater Punkt (`ROADMAP.md` Later, DataAnnotations/FluentValidation). Phase 1 macht das
  Seam, nicht die Validation selbst.
- **Keine Änderung am Tracing-Span-Modell.** Der `SleipnirCall`-Span bleibt, wie er ist (umschließt
  die Pipeline); der Telemetry-Interceptor nutzt ihn aus dem Context.
- **Kein AOT.** Reflection/`Expression.Compile` bleibt. Nicht Phase 1.
- **Keine typisierten Client-Exceptions.** Das ist der Codegen-Teil, der mit Phase 3-B kommt.

---

## Erfolgskriterium (aus `ROADMAP.md` Phase 1)

- [ ] `RequireAuthentication` + Policy-basierte Auth via Pipeline
- [ ] `sleipnir.*`-Metrics in OTLP (`Meter "Sleipnir"`)
- [ ] `ERROR_CATALOG.md` mit stabilen Codes + `SleipnirError.Category`
- [ ] JSON-RPC-Mapping auf `Category` statt String-Präfix
- [ ] Eine Fehler-Factory statt zwei (`SleipnirResults` + Invoker-Fabriken vereinheitlicht)
- [ ] Batch-Pfad läuft durch die Interceptor-Pipeline (Single- und Batch-Pfad kohärent)
- [ ] `SleipnirInvocationContext` mit `HttpContext`, `InvokeInfo`, `Response`, `Activity`
- [ ] `STABILITY.md`-Updates: Auth/Telemetry/Fehler-Interceptors von experimental → stable (nach
      Landung), `SleipnirError.Category` als additives Feld dokumentiert

---

## Implementierungs-Reihenfolge

1. `SleipnirOptions.Interceptors`/`.BatchInterceptors` Collections + DI-Wiring (Schritt 1).
2. `SleipnirInvocationContext` erweitern + `ISleipnirInterceptor`-Signatur umstellen + `ISleipnirBatchInterceptor` neu (Schritt 2).
3. `SleipnirLoggingInterceptor` auf neue Signatur migrieren (zusammen mit Schritt 2, damit die
   Pipeline nicht kaputtgeht).
4. `SleipnirErrorCodes` + `SleipnirError.Category` + Fehler-Fabriken vereinheitlichen (Schritt 4).
5. `SleipnirAuthorizationInterceptor` (Policies) + Batch-Pre-Pass auf denselben Interceptor umstellen (Schritt 3/Auth-Teil).
6. `SleipnirTelemetryInterceptor` (Metrics + Logging-Conventions) + acht `SleipnirTracing`-Stellen konsolidieren (Schritt 3/OTel-Teil).
7. Batch-Pfad an die Pipeline anbinden (Schritt 3/Batch-Teil).
8. JSON-RPC-Mapping auf `Category` umstellen.
9. `ERROR_CATALOG.md` schreiben.
10. Tests + `STABILITY.md`-Updates.