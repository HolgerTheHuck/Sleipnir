import { describe, it, expect, vi } from "vitest";
import {
  SleipnirSignalrClient,
  type IHubConnection,
  type IStreamResult,
  type IStreamSubscriber,
  type SignalrHubFactory,
} from "../../src/signalr.js";
import { SleipnirError } from "../../src/errors.js";
import { ExecutionMode } from "../../src/types.js";
import type { SleipnirRequest, SleipnirResponse } from "../../src/types.js";

// --- Fake SignalR surface ---

/** A controllable stream: the test pushes frames via `push`/`complete`/`error`. */
class FakeStream implements IStreamResult<string> {
  private observer?: { next: (v: string) => void; complete?: () => void; error?: (e: unknown) => void };
  disposed = false;
  /** The application args passed to `stream("SubscribeAsync", ...)` — [req, resumeId?, lastEventId?]. */
  readonly args: unknown[];
  constructor(args: unknown[]) {
    this.args = args;
  }
  subscribe(observer: {
    next: (v: string) => void;
    complete?: () => void;
    error?: (e: unknown) => void;
  }): IStreamSubscriber {
    this.observer = observer;
    return {
      dispose: () => {
        this.disposed = true;
        this.observer = undefined;
      },
    };
  }
  push(frame: string): void {
    this.observer?.next(frame);
  }
  complete(): void {
    this.observer?.complete?.();
    this.observer = undefined;
  }
  error(err: unknown): void {
    this.observer?.error?.(err);
    this.observer = undefined;
  }
}

/** Records invocations and lets the test respond to `invoke` / `stream`. */
class FakeHub implements IHubConnection {
  started = false;
  stopped = false;
  readonly invokeCalls: { method: string; args: unknown[] }[] = [];
  readonly streamCalls: FakeStream[] = [];
  /** Set by a test to control the next `invoke` result. */
  nextInvokeResult: unknown;
  nextInvokeError?: Error;
  onreconnectingHandler?: (e?: unknown) => void;
  onreconnectedHandler?: (c?: string) => void;
  oncloseHandler?: (e?: unknown) => void;

  constructor(opts?: { nextInvokeResult?: unknown }) {
    this.nextInvokeResult = opts?.nextInvokeResult;
  }
  async start(): Promise<void> {
    this.started = true;
  }
  async stop(): Promise<void> {
    this.stopped = true;
  }
  invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    this.invokeCalls.push({ method, args });
    if (this.nextInvokeError) return Promise.reject(this.nextInvokeError);
    return Promise.resolve(this.nextInvokeResult as T);
  }
  stream<T>(method: string, ...args: unknown[]): IStreamResult<T> {
    expect(method).toBe("SubscribeAsync");
    const s = new FakeStream(args);
    this.streamCalls.push(s);
    return s as unknown as IStreamResult<T>;
  }
  onreconnecting(h: (e?: unknown) => void): void {
    this.onreconnectingHandler = h;
  }
  onreconnected(h: (c?: string) => void): void {
    this.onreconnectedHandler = h;
  }
  onclose(h: (e?: unknown) => void): void {
    this.oncloseHandler = h;
  }
}

function frame(obj: Record<string, unknown>): string {
  return JSON.stringify(obj);
}
function eventFrame(subId: string, eventId: number, data: unknown): string {
  return frame({ type: "event", subscriptionId: subId, eventId, data });
}
function ackFrame(subId: string, replayedFrom?: number): string {
  return frame({ type: "ack", subscriptionId: subId, replayedFrom });
}

function client(hub: FakeHub): SleipnirSignalrClient {
  const factory: SignalrHubFactory = () => hub;
  return new SleipnirSignalrClient("https://localhost:5001", { hubFactory: factory, reconnect: false });
}

function baseReq(): SleipnirRequest {
  return { controller: "HubStreamEvent", method: "Tick", id: "s1" };
}

/** `subscribe`/`resume` await `connect()` before opening the stream, so the FakeStream lands in
 * `hub.streamCalls` one microtask after the call returns. Flush before grabbing it. */
const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

/** Grabs the most recently opened stream after a microtask flush. */
async function lastStream(hub: FakeHub): Promise<FakeStream> {
  await flush();
  const s = hub.streamCalls[hub.streamCalls.length - 1];
  if (!s) throw new Error("no stream was opened");
  return s;
}

// --- tests ---

describe("SleipnirSignalrClient", () => {
  it("subscribes: ack resolves the handle, then events drive onNext with a live lastEventId cursor", async () => {
    const hub = new FakeHub();
    const c = client(hub);
    await c.connect();

    const seen: unknown[] = [];
    let completed = false;
    const sub = c.subscribe<string>(
      baseReq(),
      { onNext: (v) => seen.push(v), onComplete: () => (completed = true) },
    );

    const stream = await lastStream(hub);
    expect(stream.args[0]).toEqual(baseReq());
    expect(stream.args[1]).toBeNull(); // fresh: resumeId = null
    expect(stream.args[2]).toBeNull(); // fresh: lastEventId = null

    // ack → resolves the handle with the subscriptionId.
    stream.push(ackFrame("sub-1"));
    const handle = await sub;
    expect(handle.subscriptionId).toBe("sub-1");
    expect(handle.lastEventId).toBe(0);

    stream.push(eventFrame("sub-1", 1, "a"));
    stream.push(eventFrame("sub-1", 2, "b"));
    expect(seen).toEqual(["a", "b"]);
    expect(handle.lastEventId).toBe(2);

    // terminal complete frame → onComplete, no further delivery.
    stream.push(frame({ type: "complete", subscriptionId: "sub-1" }));
    expect(completed).toBe(true);
    stream.push(eventFrame("sub-1", 3, "c"));
    expect(seen).toEqual(["a", "b"]); // ignored after complete

    await c.close();
  });

  it("dedups replayed events (eventId <= cursor) from the at-least-once replay", async () => {
    const hub = new FakeHub();
    const c = client(hub);
    await c.connect();

    const seen: unknown[] = [];
    const sub = c.subscribe<string>(baseReq(), { onNext: (v) => seen.push(v) });
    const stream = await lastStream(hub);
    stream.push(ackFrame("sub-2"));
    await sub;

    stream.push(eventFrame("sub-2", 5, "x"));
    stream.push(eventFrame("sub-2", 5, "x-dup")); // same eventId → dropped
    stream.push(eventFrame("sub-2", 4, "old")); // older eventId → dropped
    stream.push(eventFrame("sub-2", 6, "y"));
    expect(seen).toEqual(["x", "y"]);
    await c.close();
  });

  it("resumes: streams with (req, subscriptionId, lastEventId) and the ack carries replayedFrom", async () => {
    const hub = new FakeHub();
    const c = client(hub);
    await c.connect();

    const seen: unknown[] = [];
    const sub = c.resume<string>("durable-9", 7, { onNext: (v) => seen.push(v) });
    const stream = await lastStream(hub);
    expect(stream.args[1]).toBe("durable-9");
    expect(stream.args[2]).toBe(7);

    stream.push(ackFrame("durable-9", 8)); // replayedFrom = 8 (first replayed eventId)
    const handle = await sub;
    expect(handle.subscriptionId).toBe("durable-9");
    expect(handle.lastEventId).toBe(7); // cursor preserved until an event advances it

    stream.push(eventFrame("durable-9", 8, "gap-b"));
    stream.push(eventFrame("durable-9", 9, "live-c"));
    expect(seen).toEqual(["gap-b", "live-c"]);
    expect(handle.lastEventId).toBe(9);
    await c.close();
  });

  it("rejects the subscribe promise when the stream errors before the ack", async () => {
    const hub = new FakeHub();
    const c = client(hub);
    await c.connect();

    const sub = c.subscribe<string>(baseReq(), { onNext: () => undefined });
    const stream = await lastStream(hub);
    stream.error(new Error("auth-rejected"));
    await expect(sub).rejects.toThrow("auth-rejected");
    await c.close();
  });

  it("rejects the subscribe promise on an ack frame missing subscriptionId", async () => {
    const hub = new FakeHub();
    const c = client(hub);
    await c.connect();

    const sub = c.subscribe<string>(baseReq(), { onNext: () => undefined });
    const ackMissing = await lastStream(hub);
    ackMissing.push(frame({ type: "ack" }));
    await expect(sub).rejects.toThrow("missing subscriptionId");
    await c.close();
  });

  it("unsubscribe disposes the stream-sub (sends a stream Cancel → server Detach)", async () => {
    const hub = new FakeHub();
    const c = client(hub);
    await c.connect();

    const sub = c.subscribe<string>(baseReq(), { onNext: () => undefined });
    const stream = await lastStream(hub);
    stream.push(ackFrame("sub-3"));
    const handle = await sub;
    expect(stream.disposed).toBe(false);
    await handle.unsubscribe();
    expect(stream.disposed).toBe(true);
    await c.close();
  });

  it("routes calls through DoWork and normalizes the response", async () => {
    const hub = new FakeHub({ nextInvokeResult: { code: 200, data: { ok: true }, id: "s1" } });
    const c = client(hub);
    await c.connect();

    const req = baseReq();
    const resp = await c.call(req);
    expect(hub.invokeCalls[0].method).toBe("DoWork");
    expect(hub.invokeCalls[0].args[0]).toBe(req);
    expect(resp.code).toBe(200);
    expect(resp.isSuccess).toBe(true);
    expect(resp.data).toEqual({ ok: true });
    await c.close();
  });

  it("routes batches through DoWorkMany with {requests, mode}", async () => {
    const hub = new FakeHub({ nextInvokeResult: [{ code: 200, data: 1 }, { code: 200, data: 2 }] });
    const c = client(hub);
    await c.connect();

    const reqs = [baseReq(), { controller: "C", method: "M", id: "s2" }];
    const resps = await c.callBatch(reqs, ExecutionMode.Serial);
    expect(hub.invokeCalls[0].method).toBe("DoWorkMany");
    const multi = hub.invokeCalls[0].args[0] as { requests: unknown[]; mode: ExecutionMode };
    expect(multi.requests).toBe(reqs);
    expect(multi.mode).toBe(ExecutionMode.Serial);
    expect(resps.map((r) => r.data)).toEqual([1, 2]);
    await c.close();
  });

  it("throws a clear install hint when @microsoft/signalr is not loadable (default factory)", async () => {
    // The default factory dynamic-imports @microsoft/signalr; the test workspace does not install
    // it (optional peer), so the import rejects and the factory throws the install hint. A
    // non-literal specifier keeps tsc from trying to resolve the module (it is not installed).
    const modName: string = "@microsoft/signalr";
    const failing: SignalrHubFactory = async () => {
      let mod: any;
      try {
        mod = await import(/* @vite-ignore */ modName);
      } catch {
        throw new SleipnirError(0, "install hint");
      }
      return mod;
    };
    const c = new SleipnirSignalrClient("https://localhost:5001", {
      hubFactory: failing,
      reconnect: false,
    });
    await expect(c.connect()).rejects.toThrow("install hint");
  });

  it("reconnects a durable subscription (resume) on onreconnected", async () => {
    const hub = new FakeHub();
    const c = client(hub);
    await c.connect();

    let policyDecision: "fresh" | "resume" | "drop" = "resume";
    const seen: unknown[] = [];
    const sub = c.subscribe<string>(
      { controller: "HubStreamEvent", method: "Tick", id: "s1" },
      { onNext: (v) => seen.push(v) },
      { resumePolicy: () => policyDecision },
    );
    const stream1 = await lastStream(hub);
    stream1.push(ackFrame("durable-7"));
    const handle = await sub;
    stream1.push(eventFrame("durable-7", 1, "a"));
    expect(handle.lastEventId).toBe(1);

    // Simulate a reconnect: onreconnecting fires (flag flips), the old stream tears down, then
    // onreconnected fires and re-streams the sub per the resume policy.
    hub.onreconnectingHandler?.(new Error("connection lost"));
    stream1.error(new Error("connection lost"));
    hub.onreconnectedHandler?.("new-conn-id");

    // A new stream is opened in resume mode with the durable id + cursor.
    const stream2 = await lastStream(hub);
    expect(stream2.args[1]).toBe("durable-7");
    expect(stream2.args[2]).toBe(1); // lastEventId cursor carried into the resume
    stream2.push(ackFrame("durable-7", 2));
    stream2.push(eventFrame("durable-7", 2, "b"));
    expect(seen).toEqual(["a", "b"]);
    expect(handle.subscriptionId).toBe("durable-7");
    await c.close();
  });
});