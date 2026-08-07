# Phase 3 — Events / Server-Push Design

> Roadmap: `ROADMAP.md` → Benutzbarkeit-Roadmap → Phase 3 (gekoppelter Durchgang).
> Status: **entworfen**. Noch nicht implementiert.
>
> Phase 3 baut **Server→Client-Push** als First-Class-Oberfläche und **Client-Test-Doubles**
> (B) als kohärente Ergänzung. Beide sind gekoppelt, weil Events eine Codegen-Erweiterung
> brauchen (typisierte Subscribe-Oberfläche) — genau dann ist B billig.

---

## Bestand (Fakten)

- **WS-Transport** (`TrameWebSocket/TrameWebSocketMiddleware.cs`): Request/Response. Liest
  JSON-Request, ruft `InvokeDi`, schreibt JSON-Response. Kein Push-Kanal.
- **SignalR-Hub** (`TrameHub/Hub/TrameHub.cs`): `DoWork`/`DoWorkMany` (Request/Response).
  SignalR *kann* Push (`Clients.User(...).SendAsync`), aber Trame nutzt es heute nicht.
- **Discovery** (`TrameCore/Services/TrameDiscoveryService.cs`): deklariert `kind: "stream"`
  für `IAsyncEnumerable<T>`. Kein `kind: "event"`.
- **Codegen** (`Trame.Codegen.Core`, `clients/codegen`): emittiert typisierte Call-Oberflächen.
  Keine Subscribe-Oberfläche.

---

## Architektur: Events als neues Oberflächen-Modell neben Calls

Calls = Request/Response (Single, Batch, Chain). Events = Server→Client-Push, unendlich bis
Unsubscribe. Zwei verschiedene Dinge — nicht vermischen, auch wenn beide "viele Nachrichten
über WS" heißen.

### Server-Oberfläche

```csharp
[TrameController("Chat")]
public class ChatController(IChatService service)
{
    [TrameMethod("SendMessage")] public Task<Message> SendMessage(...) { ... }

    // NEU: parametrisierte Subscribe-Methode. Gibt einen IObservable<T> zurück.
    // Parameter sind First-Class (hier chatId) — dominiert in der Praxis ("alle in Chat X").
    [TrameEvent("MessageReceived")]
    public IObservable<Message> Subscribe(int chatId, CancellationToken ct)
        => service.SubscribeMessages(chatId, ct);
}
```

- `[TrameEvent]` markiert eine Subscribe-Methode (neues Attribut, Analog zu `[TrameMethod]`).
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
   - (b) `Last-Event-Id`-Resume — v1.x+
   - (c) Server-Buffer — v1.x+
   - **Entscheidung 2: (a) für v1, (b) v1.x+.**
4. **Auth pro Subscription**: Subscribe-Call läuft durch Auth (wie jeder Call). Aber die
   Subscription ist lange lebendig — Rolle/Policy können sich ändern. Mindestens: Auth zur
   Subscribe-Zeit prüfen. Besser (v1.x): Re-Check bei Reconnect.
   - **Entscheidung 3: Auth zur Subscribe-Zeit für v1; Re-Check bei Reconnect v1.x+.**

---

## Kompositionsregel (früh festnageln)

- **Events sind *nicht* chainbar** (wie Streams auch). `Exposes("$.id", …)` braucht einen
  fertigen Response; ein Event-Stream hat keinen. Einfache, konsistente Regel:
  *Call-Results können exponiert werden; Streams/Events nicht.*
- **Events ≠ Streaming-Response**: Streaming = ein Call mit vielen Elementen (endlich, dann
  fertig). Events = unendlicher Push bis Unsubscribe. Zwei verschiedene Dinge — nicht vermischen.
- Compile-Fehler im Codegen, wenn jemand versucht, ein Event in einer Batch-Chain zu nutzen.

---

## Client-Test-Doubles (B) — im Events-Codegen-Design

- Generierte Clients gegen eine mockbare `ITrameClient`-Schnittstelle + In-Memory-Test-Transport.
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
eigenes `TrameSubscription<T>`; C# → `IObservable<T>`.

### Entscheidung 2 — Gap-Semantik: at-most-once-while-disconnected (v1)
v1: gap-akzeptierend, dokumentiert ("at-most-once-while-disconnected"). `Last-Event-Id`-Resume
ist v1.x+. Jede Event-Frame trägt trotzdem `eventId` (monoton), damit Resume später möglich wird.

### Entscheidung 3 — Auth zur Subscribe-Zeit (v1)
v1: Auth zur Subscribe-Zeit (wie jeder Call). Re-Check bei Reconnect ist v1.x+. Auth-Interceptor
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
Server puffert begrenzt (Default z. B. 100 Events pro Subscription, via `TrameOptions`).
Wenn voll, droppt die ältesten. Eine `trame.event.dropped`-Metrik zählt. DoS-sicher,
deterministisch, dokumentiert "at-most-once" implizit. Block (Server blockiert bis Client
liest) ist riskant bei WS (Server-Thread blockiert, Producer blockiert) — nicht gewählt.
Disconnect bei vollem Puffer ist hart — nicht gewählt.

### Entscheidung 8 — Wire-Format: Separater Frame-Typ
Event-Frame ist ein separater Frame-Typ auf dem Wire:
```json
{"type":"event","subscriptionId":"...","eventId":42,"data":{...}}
```
Klar von `TrameResponse` getrennt (Calls vs. Events). Erweiterbar für zukünftige Frame-Typen
(`type:"complete"`, `type:"error"` für Subscription-Ende). Subscribe-/Unsubscribe-Requests
bleiben `TrameRequest` (mit `kind:"subscribe"`-Feld oder separatem Dispatcher-Pfad);
Subscribe-Response ist eine `TrameResponse`, die die `subscriptionId` trägt.

---

## Abgrenzung (was Phase 3 *nicht* macht)

- **Kein `Last-Event-Id`-Resume** (v1.x+).
- **Kein Server-Buffer bei Disconnect** (v1.x+).
- **Keine Bidirektionales Streaming** (Client→Server-Push) — das ist ein separates Modell.
- **Keine SignalR-Events in v1** (WS-only; SignalR folgt).
- **Kein REST-Long-Polling.**

---

## Implementierungs-Schritte (Entwurf)

1. `[TrameEvent]`-Attribut + `IObservable<T>`-Erkennung in Discovery (`kind: "event"`). ✓
2. WS-Transport: Subscribe/Unsubscribe-Handling + Event-Push-Kanal (Text-Frame pro Event). ✓
3. Server: Subscription-Manager (subscriptionId, pro-Connection, Auto-Cleanup bei Disconnect). ✓ (in 2 integriert)
4. Codegen: typisierte Subscribe-Oberfläche (TS `Observable<T>` / `TrameSubscription<T>`). **post-Phase-3-v1** — Codegen-Baum ist substantial, eigener Schritt.
5. Client-Test-Doubles: mockbare `ITrameClient` + In-Memory-Test-Transport. **post-Phase-3-v1** — eigener Schritt.
6. Reconnect → Resubscribe im WS-Client (auto). **post-Phase-3-v1** — braucht Subscribe/Unsubscribe im TrameWebSocketClient + Reconnect-Logik.
7. Doku: Events-Lifecycle, Kompositionsregel, at-most-once-Garantie, WS/SignalR-only. ✓ (STABILITY §2)
8. Tests + `STABILITY.md`-Updates. ✓ (3 Integration-Tests: Subscribe/Events/complete, Unsubscribe, NonObservable-400)

## Phase 3 v1 — geliefert (Server-Seite)

- `[TrameEvent]`-Attribut + Discovery `kind:"event"` (Schritt 1)
- `ITrameCore.SubscribeAsync` + `TrameInvoker.SubscribeAsync` (Resolve + Auth + Bind → IObservable)
- `TrameSubscriptionManager` (pro-Connection, bounded Channel + drop-oldest, Send-Loop, Auto-Cleanup)
- WS-Dispatcher: `kind:"subscribe"`/`kind:"unsubscribe"`-Erkennung, Event/complete/error-Frames
- `trame.event.dropped`-Metrik (Backpressure)
- 3 Integration-Tests (Subscribe+Events+complete, Unsubscribe, NonObservable-400)
- STABILITY.md §2: Events als experimental deklariert

## Phase 3 v1 — offen (Client-Seite, post-Phase-3-v1)

- **Codegen typisierte Subscribe-Oberfläche** (Schritt 4) — TS/C#-Emitter-Erweiterung
- **Client-Test-Doubles** (Schritt 5) — mockbare ITrameClient + In-Memory-Transport
- **Reconnect → Resubscribe im WS-Client** (Schritt 6) — Subscribe/Unsubscribe im TrameWebSocketClient + Reconnect-Logik
- **SignalR-Events** (v1.x+) — WS-only in v1
- **Last-Event-Id-Resume** (v1.x+)