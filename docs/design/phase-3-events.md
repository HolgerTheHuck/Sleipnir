# Phase 3 — Events / Server-Push Design

> Roadmap: `ROADMAP.md` → Benutzbarkeit-Roadmap → Phase 3 (gekoppelter Durchgang).
> Status: **geliefert (Server-Seite, v1)** — siehe „Phase 3 v1 — geliefert (Server-Seite)" unten.
>
> **Vertrags-Korrektur (1.2.0):** `[SleipnirEvent]` ist der erforderliche Marker für Event-Methoden
> (wie in diesem Entwurf vorgesehen). Die ursprüngliche Implementierung (1.1.0) hat das Attribut
> definiert, aber zur Laufzeit nie gelesen — Events wurden über `[SleipnirMethod]` + Rückgabe-Typ
> `IObservable<T>` registriert/discoveryiert. Ab 1.2.0 scannt `Register` `[SleipnirEvent]`, validiert
> die `IObservable<T>`-Rückgabe zur Registrierungszeit, und lehnt `IObservable<T>`-Methoden mit
> `[SleipnirMethod]` ab. Consumer-Doku: `README_DETAILS.md` → „Server-Push Events"; Wire-Spec:
> `PROTOCOL.md` → „Server-Push Events".
>
> Phase 3 baut **Server→Client-Push** als First-Class-Oberfläche und **Client-Test-Doubles**
> (B) als kohärente Ergänzung. Beide sind gekoppelt, weil Events eine Codegen-Erweiterung
> brauchen (typisierte Subscribe-Oberfläche) — genau dann ist B billig.

---

## Bestand (Fakten)

- **WS-Transport** (`SleipnirWebSocket/SleipnirWebSocketMiddleware.cs`): Request/Response. Liest
  JSON-Request, ruft `InvokeDi`, schreibt JSON-Response. Kein Push-Kanal.
- **SignalR-Hub** (`SleipnirHub/Hub/SleipnirHub.cs`): `DoWork`/`DoWorkMany` (Request/Response).
  SignalR *kann* Push (`Clients.User(...).SendAsync`), aber Sleipnir nutzt es heute nicht.
- **Discovery** (`SleipnirCore/Services/SleipnirDiscoveryService.cs`): deklariert `kind: "stream"`
  für `IAsyncEnumerable<T>`. Kein `kind: "event"`.
- **Codegen** (`Sleipnir.Codegen.Core`, `clients/codegen`): emittiert typisierte Call-Oberflächen.
  Keine Subscribe-Oberfläche.

---

## Architektur: Events als neues Oberflächen-Modell neben Calls

Calls = Request/Response (Single, Batch, Chain). Events = Server→Client-Push, unendlich bis
Unsubscribe. Zwei verschiedene Dinge — nicht vermischen, auch wenn beide "viele Nachrichten
über WS" heißen.

### Server-Oberfläche

```csharp
[SleipnirController("Chat")]
public class ChatController(IChatService service)
{
    [SleipnirMethod("SendMessage")] public Task<Message> SendMessage(...) { ... }

    // NEU: parametrisierte Subscribe-Methode. Gibt einen IObservable<T> zurück.
    // Parameter sind First-Class (hier chatId) — dominiert in der Praxis ("alle in Chat X").
    [SleipnirEvent("MessageReceived")]
    public IObservable<Message> Subscribe(int chatId, CancellationToken ct)
        => service.SubscribeMessages(chatId, ct);
}
```

- `[SleipnirEvent]` markiert eine Subscribe-Methode (neues Attribut, Analog zu `[SleipnirMethod]`).
- Rückgabe `IObservable<T>` (oder `IAsyncEnumerable<T>` — siehe Entscheidung 1).
- Parameter werden zur Subscribe-Zeit übergeben (parametrisierte Subscription).

### Wire (neue Nachrichtentypen neben Request/Response)

- **Subscribe-Request**: Client→Server, wie ein Call, aber mit einem Flag (`kind: "subscribe"`)
  oder einem separaten Controller-Method-Typ in der Discovery.
- **Event-Frame**: Server→Client, trägt `subscriptionId` + `eventId` + `data`. Kein `id`-Match
  mit Calls (eigene Korrelation).
- **Unsubscribe-Request**: Client→Server, `subscriptionId`.
- **Subscribe-Response**: Server→Client, bestätigt die Subscription, trägt `subscriptionId`.

### Transport-Story

- **WS + SignalR**: ja, native Push.
- **REST**: nein. Kein Long-Polling. Klares Statement: "Events sind WS/SignalR-only; ein
  REST-Client ruft `GetMessages` periodisch oder nutzt WS."

---

## Subscription-Lifecycle (die wichtigsten Design-Punkte)

1. **Subscribe/Unsubscribe-Symmetrie**: jeder `subscribe` bekommt eine `subscriptionId`,
   `unsubscribe` gibt frei. Sonst leckt serverseitige Ressource.
2. **Reconnect → Resubscribe**: WS-Clients mit Auto-Reconnect müssen ihre Subscriptions
   **automatisch** wiederherstellen — sonst "verpasse" ich Events. Mindest-ebenbürtig zu
   SignalR-native. (Client-seitig; Server muss Re-Subscribe erlauben.)
3. **Gap-Semantik beim Reconnect**: Events während des Drops — drei Optionen:
   - (a) gap-akzeptierend (einfach weiter) — **v1, dokumentiert** ("at-most-once-while-disconnected")
   - (b) `Last-Event-Id`-Resume — v1.x+ → **geliefert als Phase R (experimental)** für opt-in
     `[SleipnirEvent(Resumable = true)]`: Client schickt `lastEventId`, Server replayed die Lücke
     aus einem pro-durable-Subscription Ring-Buffer (at-least-once innerhalb des Fensters; Client
     dedup'd per `eventId`).
   - (c) Server-Buffer — v1.x+ → **geliefert als Phase R** (der Ring-Buffer aus (b)).
   - **Entscheidung 2: (a) für v1, (b)/(c) geliefert als Phase R (experimental, opt-in `Resumable`).**
4. **Auth pro Subscription**: Subscribe-Call läuft durch Auth (wie jeder Call). Aber die
   Subscription ist lange lebendig — Rolle/Policy können sich ändern. Mindestens: Auth zur
   Subscribe-Zeit prüfen. Besser (v1.x): Re-Check bei Reconnect.
   - **Entscheidung 3: Auth zur Subscribe-Zeit für v1; Re-Check bei Reconnect v1.x+ → geliefert als
     Phase R3.** Ein Resume re-prüft die Auth gegen die ORIGINAL-Route (serverseitig bei Erzeugung
     hinterlegt, nicht client-behauptet); 401/403 auf Resume → Durable-Subscription wird
     abgerissen und der Fehler zurückgegeben (kein stilles Resume nach widerrufener Rolle).

---

## Kompositionsregel (früh festnageln)

- **Events sind *nicht* chainbar** (Streams dagegen schon). `Exposes("$.id", …)` braucht einen
  fertigen Response; ein Event-Stream hat keinen. Streams (`IAsyncEnumerable<T>`) sind Calls,
  die serverseitig zu einem fertigen JSON-Array materialisiert werden — deshalb läuft ihr
  Ergebnis wie jedes Call-Result durch `ExecuteAuthorized` und kann `Exposes`/`@alias` füttern
  (z. B. `$.hits[*].articleId` über einen paginierten Stream). Events gehen über den separaten
  `SubscribeAsync`-Pfad, haben kein `DependencyMapping`/`ExposedDependencies` und können nicht
  chainen. Einfache, konsistente Regel: *Call- und Stream-Results können exponiert werden;
  Events nicht.*
- **Events ≠ Streaming-Response**: Streaming = ein Call mit vielen Elementen (endlich, dann
  fertig). Events = unendlicher Push bis Unsubscribe. Zwei verschiedene Dinge — nicht vermischen.
- Compile-Fehler im Codegen, wenn jemand versucht, ein Event in einer Batch-Chain zu nutzen.

---

## Client-Test-Doubles (B) — im Events-Codegen-Design

- Generierte Clients gegen eine mockbare `ISleipnirClient`-Schnittstelle + In-Memory-Test-Transport.
- Bei Events-Codegen-Erweiterung gleich designen: die typisierte Subscribe-Oberfläche wird
  mockbar (z. B. `IObservable<T>` im Test durch ein Subject ersetzt).
- **B ist kein separater Punkt, er gehört in 3's Codegen-Design.**

---

## Getroffene Entscheidungen (2026-08-07)

### Entscheidung 1 — Rückgabe: `IObservable<T>` oder `IAsyncEnumerable<T>`?
**`IObservable<T>` für v1.** `IAsyncEnumerable<T>` ist heute schon für materialisierte Streams
(`kind: "stream"`) belegt. Events brauchen Push-Semantik (Server treibt, Client empfängt);
`IObservable<T>` ist das natürliche Push-Modell. `IAsyncEnumerable<T>` für Events würde mit dem
bestehenden `kind: "stream"` kollidieren. Codegen: TS → `Observable<T>` (rxjs-kompatibel) oder
eigenes `SleipnirSubscription<T>`; C# → `IObservable<T>`.

### Entscheidung 2 — Gap-Semantik: at-most-once-while-disconnected (v1)
v1: gap-akzeptierend, dokumentiert ("at-most-once-while-disconnected"). Jede Event-Frame trägt
`eventId` (monoton pro Subscription). **Phase R (experimental, opt-in
`[SleipnirEvent(Resumable = true)]`):** `Last-Event-Id`-Resume + Server-Buffer geliefert — der
Client schickt `lastEventId`, der Server replayed die Lücke aus einem pro-durable-Subscription
Ring-Buffer (at-least-once innerhalb des Replay-Fensters; Client dedup'd per `eventId`).
Überlauf jenseits des Fensters bleibt verloren und wird in `sleipnir.event.dropped` gezählt.

### Entscheidung 3 — Auth zur Subscribe-Zeit (v1)
v1: Auth zur Subscribe-Zeit (wie jeder Call). **Phase R3 (experimental):** Re-Check bei Reconnect
geliefert — ein Resume re-prüft die Auth gegen die ORIGINAL-Route (serverseitig bei Erzeugung
hinterlegt, nicht client-behauptet); 401/403 auf Resume → Durable-Subscription wird abgerissen
und der Fehler zurückgegeben (kein stilles Resume nach widerrufener Rolle). Auth-Interceptor
(Phase 1) läuft für Subscribe-Requests wie gehabt.

### Entscheidung 4 — Discovery-`kind: "event"`
Discovery deklariert Subscribe-Methoden als `kind: "event"` (neu, Analog zu `kind: "stream"`).
Element-Typ aus `IObservable<T>`. Parameter wie Call-Methoden.

### Entscheidung 5 — WS-only für v1, SignalR folgt
v1: WS-Transport bekommt den Push-Kanal (Text-Frame pro Event). SignalR folgt später (hat
eingebauten Push, aber andere Hub-Methode-Form). REST: nein.

### Entscheidung 6 — Reconnect: subscriptionId pro-Connection + client-side Re-Subscribe
Reconnect = neue Connection = neue `subscriptionId`s. Der Client merkt sich die Subscribe-
Parameter und ruft nach Reconnect automatisch wieder `subscribe` auf. Der Server hält
Subscriptions **pro Connection** (Auto-Cleanup bei Disconnect). Entspricht at-most-once-
while-disconnected (Entscheidung 2) — Gap-Events während Drop gehen verloren, dokumentiert.
Voraussetzung für `Last-Event-Id`-Resume (v1.x+): der Client schickt die letzte `eventId` mit,
der Server replayed ab dort (post-Phase-3).

### Entscheidung 7 — Backpressure: Bounded Buffer + Drop-oldest mit Metrik
Server puffert begrenzt (Default z. B. 100 Events pro Subscription, via `SleipnirOptions`).
Wenn voll, droppt die ältesten. Eine `sleipnir.event.dropped`-Metrik zählt. DoS-sicher,
deterministisch, dokumentiert "at-most-once" implizit. Block (Server blockiert bis Client
liest) ist riskant bei WS (Server-Thread blockiert, Producer blockiert) — nicht gewählt.
Disconnect bei vollem Puffer ist hart — nicht gewählt.

### Entscheidung 8 — Wire-Format: Separater Frame-Typ
Event-Frame ist ein separater Frame-Typ auf dem Wire:
```json
{"type":"event","subscriptionId":"...","eventId":42,"data":{...}}
```
Klar von `SleipnirResponse` getrennt (Calls vs. Events). Erweiterbar für zukünftige Frame-Typen
(`type:"complete"`, `type:"error"` für Subscription-Ende). Subscribe-/Unsubscribe-Requests
bleiben `SleipnirRequest` (mit `kind:"subscribe"`-Feld oder separatem Dispatcher-Pfad);
Subscribe-Response ist eine `SleipnirResponse`, die die `subscriptionId` trägt.

---

## Abgrenzung (was Phase 3 *nicht* macht)

- **`Last-Event-Id`-Resume + Server-Buffer:** als **Phase R (experimental, opt-in
  `[SleipnirEvent(Resumable = true)]`) geliefert** — at-least-once innerhalb des Replay-Fensters,
  mit Reconnect-Auth-Re-Check (Phase R3) und prozess-lokalem Durable-Store. Außerhalb des
  Fensters (Überlauf bei langer Disconnect-Lücke) bleiben Events verloren. Genau-once
  (braucht per-Event-Acks) und Cross-Process-Durable (über Server-Restart) bleiben future.
- **Kein Bidirektionales Streaming** (Client→Server-Push) — das ist ein separates Modell.
- **Keine SignalR-Events in v1** (WS-only; SignalR folgt).
- **Kein REST-Long-Polling.**

---

## Implementierungs-Schritte (Entwurf)

1. `[SleipnirEvent]`-Attribut + `IObservable<T>`-Erkennung in Discovery (`kind: "event"`). ✓
2. WS-Transport: Subscribe/Unsubscribe-Handling + Event-Push-Kanal (Text-Frame pro Event). ✓
3. Server: Subscription-Manager (subscriptionId, pro-Connection, Auto-Cleanup bei Disconnect). ✓ (in 2 integriert)
4. Codegen: typisierte Subscribe-Oberfläche (TS `Observable<T>` / `SleipnirSubscription<T>`). **post-Phase-3-v1** — Codegen-Baum ist substantial, eigener Schritt.
5. Client-Test-Doubles: mockbare `ISleipnirClient` + In-Memory-Test-Transport. **post-Phase-3-v1** — eigener Schritt.
6. Reconnect → Resubscribe im WS-Client (auto). **post-Phase-3-v1** — braucht Subscribe/Unsubscribe im SleipnirWebSocketClient + Reconnect-Logik.
7. Doku: Events-Lifecycle, Kompositionsregel, at-most-once-Garantie, WS/SignalR-only. ✓ (STABILITY §2)
8. Tests + `STABILITY.md`-Updates. ✓ (3 Integration-Tests: Subscribe/Events/complete, Unsubscribe, NonObservable-400)

## Phase 3 v1 — geliefert (Server-Seite)

- `[SleipnirEvent]`-Attribut + Discovery `kind:"event"` (Schritt 1)
- `ISleipnirCore.SubscribeAsync` + `SleipnirInvoker.SubscribeAsync` (Resolve + Auth + Bind → IObservable)
- `SleipnirSubscriptionManager` (pro-Connection, bounded Channel + drop-oldest, Send-Loop, Auto-Cleanup)
- WS-Dispatcher: `kind:"subscribe"`/`kind:"unsubscribe"`-Erkennung, Event/complete/error-Frames
- `sleipnir.event.dropped`-Metrik (Backpressure)
- 3 Integration-Tests (Subscribe+Events+complete, Unsubscribe, NonObservable-400)
- STABILITY.md §2: Events als experimental deklariert

## Phase R — geliefert (Last-Event-Id-Resume, experimental)

Opt-in two-axis: Server `[SleipnirEvent(Resumable = true)]` + Client-Resume-Policy
(`ResumeDecision { Fresh, Resume, Drop }`). At-least-once innerhalb des Replay-Fensters;
Client dedup'd per `eventId`; `subscriptionId` durable (stabil über Reconnects).

- **R1 — Server-Durable-Store + Replay:** `SleipnirSubscriptionStore` (SleipnirCore, DI-Singleton)
  hält pro-durable-Subscription: gehaltene `IObservable`-Source-Subscription, stabiler monotoner
  `eventId`-Counter (nicht resettet), bounded Replay-Ring-Buffer (evict-oldest →
  `sleipnir.event.dropped`), Live-Tap attach/detach, TTL+Cap-GC. `SleipnirSubscriptionManager`
  brancht auf `result.Resumable`: durable create/resume (Detach bei Disconnect — Source+Buffer
  bleiben) vs. unveränderter ephemeraler v1-Pfad; `DurableEventObserver`. WS-Middleware extrahiert
  optionale `lastEventId`+`subscriptionId` aus dem Subscribe-Frame, surft `replayedFrom` in der
  Resume-Response. 3 `SleipnirOptions`-Knobs (`EventReplayBufferCapacity` fb 1000, `EventResumeTtl`
  fb 60s, `EventMaxDurableSubscriptions` fb 10k). 9 Unit- + 2 Integration-Tests.
- **R2 — Client-Hook + `eventId`-Dedup (C# + TS):** `ResumeDecision.cs` (enum + context + policy-
  delegate), wired via `SleipnirWebSocketClient`-Ctor `resumePolicy` + per-`SubscribeAsync`.
  `TryDispatchEventFrame` capture'd `eventId`; `SleipnirSubscriptionHandler<T>` dedup'd
  (`eventId <= lastSeen → drop`) + `LastEventId`-Cursor. `ResubscribeAllAsync` Fresh/Resume/Drop
  (Resume pre-registriert den Handler unter der durable Id — Race-Fix für Replay-Frames vor der
  asynchronen Post-Response-Registrierung); degrade-to-fresh (neue Id) resettet den Cursor.
  TS-Spiegel in `websocket.ts` (`ResumeDecision`/`SubscriptionResumeContext`/`ResumePolicy` +
  `SubscribeOptions`, `onResume`, `ActiveSubscription.lastEventId`, `dispatchEventFrame`-dedup,
  `resubscribeAll`+`resubscribeResume`). Bugfix: `_subscribeRequests` war nach `requestId`
  geschlüsselt, wurde aber per `subscriptionId` gesucht → Leak; jetzt per `subscriptionId`. 5 C# +
  5 TS Resume-Tests.
- **R3 — Reconnect-Auth-Re-Check + E2E:** `ISleipnirCore.AuthorizeSubscribeAsync(controller, method,
  context)` re-prüft die Auth gegen die ORIGINAL-Route (bei Erzeugung hinterlegt, nicht
  client-behauptet — kein Privilege-Eskalation über eine gelogene Route); 401/403/404 auf Resume →
  `_store.Destroy` + Fehler (kein stilles Resume nach widerrufener Rolle).
  `SleipnirTests/Integration/ResumeTests.cs` (4 E2E-Tests, je eigener Kestrel-Host): real-Client
  Gap-Replay+Dedup, Over-Cap-Verlust, TTL→Fresh, Auth-Revoke→Teardown. Suite 518/518.

## Phase 3 v1 — offen (Client-Seite, post-Phase-3-v1)

- **Codegen typisierte Subscribe-Oberfläche** (Schritt 4) — TS/C#-Emitter-Erweiterung
- **Client-Test-Doubles** (Schritt 5) — mockbare ISleipnirClient + In-Memory-Transport
- **Reconnect → Resubscribe im WS-Client** (Schritt 6) — Subscribe/Unsubscribe im SleipnirWebSocketClient + Reconnect-Logik ✓ (Phase R2)
- **SignalR-Events** (v1.x+) — WS-only in v1
- **Last-Event-Id-Resume** (v1.x+) ✓ (Phase R geliefert, experimental)
- **Genau-once / Cross-Process-Durable** (future — braucht per-Event-Acks bzw. persistenten Backend)