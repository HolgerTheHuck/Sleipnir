import { describe, it, expect, vi } from "vitest";
import { SleipnirSseClient } from "../../src/sse.js";
import type { SseFetchLike, SleipnirSseClientOptions } from "../../src/sse.js";
import { SleipnirError } from "../../src/errors.js";

// --- SSE-Block-Builder (spiegeln das Server-Wire-Format) ---

function ack(subscriptionId: string, replayedFrom?: number): string {
  const data = replayedFrom != null
    ? JSON.stringify({ subscriptionId, replayedFrom })
    : JSON.stringify({ subscriptionId });
  return `id: 0\nevent: ack\ndata: ${data}\n\n`;
}
function event(subscriptionId: string, eventId: number, data: unknown): string {
  return `id: ${eventId}\nevent: event\ndata: ${JSON.stringify({ type: "event", subscriptionId, eventId, data })}\n\n`;
}
function complete(subscriptionId: string): string {
  return `event: complete\ndata: ${JSON.stringify({ type: "complete", subscriptionId })}\n\n`;
}

/** Baut einen ReadableStream aus SSE-Blöcken. `hangAfter` → schließt nicht, Fehler bei abort. */
function sseBody(blocks: string[], opts?: { signal?: AbortSignal; hangAfter?: boolean }): ReadableStream<Uint8Array> {
  const enc = new TextEncoder();
  return new ReadableStream<Uint8Array>({
    start(controller) {
      for (const b of blocks) controller.enqueue(enc.encode(b));
      if (opts?.hangAfter) {
        opts.signal?.addEventListener(
          "abort",
          () => controller.error(Object.assign(new Error("aborted"), { name: "AbortError" })),
          { once: true },
        );
      } else {
        controller.close();
      }
    },
  });
}

interface MockResp {
  ok?: boolean;
  status?: number;
  body?: ReadableStream<Uint8Array> | null;
}

/** Mock-fetch, der eine Folge von Response-Buildern konsumiert (ein Builder pro Aufruf). */
function makeMockFetch(seq: ((init: RequestInit) => MockResp)[]): {
  fn: SseFetchLike;
  calls: { url: string; init: RequestInit }[];
} {
  const calls: { url: string; init: RequestInit }[] = [];
  let i = 0;
  const fn = vi.fn(async (url: string, init: RequestInit) => {
    calls.push({ url, init });
    const builder = seq[Math.min(i, seq.length - 1)];
    i++;
    const r = builder(init);
    return { ok: r.ok ?? true, status: r.status ?? 200, body: r.body ?? null };
  }) as unknown as SseFetchLike;
  return { fn, calls };
}

function makeClient(seq: ((init: RequestInit) => MockResp)[], opts: SleipnirSseClientOptions = {}) {
  const { fn, calls } = makeMockFetch(seq);
  const client = new SleipnirSseClient("https://host", { fetch: fn, apiPath: "api/sleipnir", ...opts });
  return { client, calls };
}

describe("SleipnirSseClient", () => {
  it("fresh subscribe delivers ack + events + complete, with bearer + json-encoded query param", async () => {
    const { client, calls } = makeClient([
      () => ({ ok: true, status: 200, body: sseBody([ack("S1"), event("S1", 1, "a"), event("S1", 2, "b"), complete("S1")]) }),
    ], { bearer: "tok" });

    const got: string[] = [];
    let done = false;
    const sub = await client.subscribe<string>("Chat", "Messages", { onNext: (v) => got.push(v), onComplete: () => { done = true; } }, { count: 3 });
    expect(typeof sub.subscriptionId).toBe("string"); // resolved from ack
    await vi.waitFor(() => expect(done).toBe(true), { timeout: 1000 });

    expect(got).toEqual(["a", "b"]);
    expect(done).toBe(true);
    // URL carries the JSON-encoded query param (count=3 → ?count=3) and the auth header.
    expect(calls[0].url).toBe("https://host/api/sleipnir/events/Chat/Messages?count=3");
    expect((calls[0].init.headers as Record<string, string>)["Authorization"]).toBe("Bearer tok");
    expect((calls[0].init.headers as Record<string, string>)["Accept"]).toBe("text/event-stream");
  });

  it("omits the Authorization header when no bearer is configured", async () => {
    const { client, calls } = makeClient([
      () => ({ ok: true, status: 200, body: sseBody([ack("S1"), complete("S1")]) }),
    ]);
    await client.subscribe("C", "M", { onNext: () => {} });
    expect((calls[0].init.headers as Record<string, string>)["Authorization"]).toBeUndefined();
  });

  it("rejects the subscribe promise on a non-2xx fresh response", async () => {
    const { client } = makeClient([() => ({ ok: false, status: 401, body: null })], { bearer: "tok" });
    await expect(client.subscribe("C", "M", { onNext: () => {} })).rejects.toBeInstanceOf(SleipnirError);
  });

  it("reconnects in resume mode with Last-Event-Id header + resume URL and dedups replayed frames", async () => {
    // First stream: ack + 3 events, then a DROP (close without a terminal frame).
    // Second stream (resume): ack with replayedFrom + a replayed dup (eventId 3) + a new live
    // event (eventId 4) + complete. The replayed dup must be deduped (not delivered again).
    const { client, calls } = makeClient([
      () => ({ ok: true, status: 200, body: sseBody([ack("S1"), event("S1", 1, "a"), event("S1", 2, "b"), event("S1", 3, "c")]) }),
      () => ({ ok: true, status: 200, body: sseBody([ack("S1", 2), event("S1", 3, "c"), event("S1", 4, "d"), complete("S1")]) }),
    ], { reconnect: true, reconnectDelays: [0], onResume: () => "resume" });

    const got: string[] = [];
    let done = false;
    const sub = await client.subscribe<string>("E", "Tick", { onNext: (v) => got.push(v), onComplete: () => { done = true; } });
    expect(sub.subscriptionId).toBe("S1");
    await vi.waitFor(() => expect(done).toBe(true), { timeout: 2000 });

    // First stream delivered a,b,c; the resume stream delivered only d (the replayed c at
    // eventId 3 was deduped against lastEventId=3). Exactly 4 onNext calls, no duplicate c.
    expect(got).toEqual(["a", "b", "c", "d"]);
    expect(calls.length).toBe(2);
    // The resume request targets the durable subscriptionId with Last-Event-Id (header + query).
    expect(calls[1].url).toBe("https://host/api/sleipnir/events/S1?lastEventId=3");
    expect((calls[1].init.headers as Record<string, string>)["Last-Event-Id"]).toBe("3");
  });

  it("reconnect drops the subscription (no second fetch) when the resume policy returns 'drop'", async () => {
    const { client, calls } = makeClient([
      () => ({ ok: true, status: 200, body: sseBody([ack("S1"), event("S1", 1, "a")]) }), // drop (no terminal)
    ], { reconnect: true, reconnectDelays: [0], onResume: () => "drop" });

    const got: string[] = [];
    let done = false;
    await client.subscribe<string>("E", "Tick", { onNext: (v) => got.push(v), onComplete: () => { done = true; } });
    await vi.waitFor(() => expect(done).toBe(true), { timeout: 1000 });
    expect(got).toEqual(["a"]);
    expect(calls.length).toBe(1); // no reconnect attempt
  });

  it("degrades a 410 resume to a fresh re-subscribe", async () => {
    // First stream drops; resume returns 410 (durable GC'd) → degrade to fresh → third call is the
    // fresh URL again and delivers a fresh ack + complete.
    const { client, calls } = makeClient([
      () => ({ ok: true, status: 200, body: sseBody([ack("S1"), event("S1", 1, "a")]) }), // drop
      () => ({ ok: false, status: 410, body: null }),                                       // resume → 410
      () => ({ ok: true, status: 200, body: sseBody([ack("S2"), complete("S2")]) }),       // fresh retry
    ], { reconnect: true, reconnectDelays: [0], onResume: () => "resume" });

    let done = false;
    const sub = await client.subscribe<string>("E", "Tick", { onNext: () => {}, onComplete: () => { done = true; } });
    expect(sub.subscriptionId).toBe("S1");
    await vi.waitFor(() => expect(done).toBe(true), { timeout: 2000 });
    expect(calls.length).toBe(3);
    // The 2nd call is the resume URL; the 3rd (after 410) is the fresh URL again.
    expect(calls[1].url).toContain("/events/S1?lastEventId=1");
    expect(calls[2].url).toBe("https://host/api/sleipnir/events/E/Tick");
  });

  it("unsubscribe aborts the in-flight stream without invoking onError/onComplete", async () => {
    // Stream sends the ack then hangs (no terminal); the body errors when the client aborts.
    const { client } = makeClient([
      (init) => ({ ok: true, status: 200, body: sseBody([ack("S1")], { signal: init.signal!, hangAfter: true }) }),
    ], { reconnect: false });

    let err: Error | undefined;
    let done = false;
    const sub = await client.subscribe<string>("C", "M", { onNext: () => {}, onError: (e) => { err = e; }, onComplete: () => { done = true; } });
    expect(sub.subscriptionId).toBe("S1");
    await sub.unsubscribe();
    // Give the abort a moment to propagate through the read loop.
    await new Promise((r) => setTimeout(r, 50));
    expect(err).toBeUndefined();
    expect(done).toBe(false);
  });
});