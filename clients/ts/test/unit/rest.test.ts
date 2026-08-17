import { describe, it, expect, vi } from "vitest";
import { SleipnirRestClient } from "../../src/rest.js";
import { SleipnirError, CancelledError } from "../../src/errors.js";
import { ExecutionMode } from "../../src/types.js";

type FetchImpl = (url: string | URL | Request, init: any) => Promise<any>;

function okResponse(body: unknown) {
  const text = typeof body === "string" ? body : JSON.stringify(body);
  return { ok: true, status: 200, text: async () => text, json: async () => JSON.parse(text) };
}

function httpError(status: number, body = "err") {
  return { ok: false, status, text: async () => body, json: async () => body };
}

/** Mock-fetch, der ein bereits abgebrochenes Signal oder ein späteres abort mit AbortError quittiert. */
function abortAwareFetch(impl: (url: string, init: any) => any): FetchImpl {
  return vi.fn(async (url: string, init: any) => {
    const signal = init?.signal;
    if (signal?.aborted) throw Object.assign(new Error("aborted"), { name: "AbortError" });
    return new Promise((resolve, reject) => {
      if (signal) {
        signal.addEventListener("abort", () =>
          reject(Object.assign(new Error("aborted"), { name: "AbortError" })),
        );
      }
      Promise.resolve(impl(url, init)).then(resolve, reject);
    });
  }) as unknown as FetchImpl;
}

function hangingFetch(): FetchImpl {
  return abortAwareFetch(() => new Promise(() => {})) as FetchImpl;
}

describe("SleipnirRestClient", () => {
  it("call() POSTet nach /api/sleipnir/json und liefert geparste Response (camelCase Body)", async () => {
    let captured: any;
    const fetch = vi.fn(async (url: string, init: any) => {
      captured = { url, init };
      return okResponse({ code: 200, data: "ok", id: "C.M", isSuccess: true });
    }) as unknown as FetchImpl;
    const c = new SleipnirRestClient("http://x", { fetch });
    const r = await c.call("C", "M", { id: 1 });
    expect(r.code).toBe(200);
    expect(r.data).toBe("ok");
    expect(captured.url).toBe("http://x/api/sleipnir/json");
    expect(captured.init.method).toBe("POST");
    const body = JSON.parse(captured.init.body);
    expect(body.controller).toBe("C");
    expect(body.method).toBe("M");
    expect(body.id).toBe("C.M");
    expect(body.dependencyMapping).toBeNull();
    expect(body.params).toEqual([
      { parameterName: "id", data: 1, num: 0 },
    ]);
  });

  it("setzt Authorization-Header bei bearer", async () => {
    let captured: any;
    const fetch = vi.fn(async (url: string, init: any) => {
      captured = init;
      return okResponse({ code: 200, isSuccess: true });
    }) as unknown as FetchImpl;
    const c = new SleipnirRestClient("http://x", { fetch, bearer: "tok" });
    await c.call("C", "M", {});
    expect(captured.headers["Authorization"]).toBe("Bearer tok");
    expect(captured.headers["Content-Type"]).toBe("application/json");
  });

  it("löst einen Function-Bearer pro Call frisch auf (rotierende JWTs)", async () => {
    const seen: string[] = [];
    let token = "v1";
    const fetch = vi.fn(async (url: string, init: any) => {
      seen.push(init.headers["Authorization"]);
      return okResponse({ code: 200, isSuccess: true });
    }) as unknown as FetchImpl;
    const c = new SleipnirRestClient("http://x", { fetch, bearer: () => token });
    await c.call("C", "M", {});
    token = "v2";
    await c.call("C", "M", {});
    expect(seen).toEqual(["Bearer v1", "Bearer v2"]);
  });

  it("setBearer tauscht den Token zur Laufzeit (String und Funktion)", async () => {
    const seen: string[] = [];
    const fetch = vi.fn(async (url: string, init: any) => {
      seen.push(init.headers["Authorization"]);
      return okResponse({ code: 200, isSuccess: true });
    }) as unknown as FetchImpl;
    const c = new SleipnirRestClient("http://x", { fetch, bearer: "a" });
    await c.call("C", "M", {});
    c.setBearer("b");
    await c.call("C", "M", {});
    c.setBearer(() => "c");
    await c.call("C", "M", {});
    expect(seen).toEqual(["Bearer a", "Bearer b", "Bearer c"]);
  });

  it("discover() GETet /api/sleipnir/discovery", async () => {
    let captured: any;
    const fetch = vi.fn(async (url: string, init: any) => {
      captured = { url, init };
      return okResponse({ controllers: [], types: {} });
    }) as unknown as FetchImpl;
    const c = new SleipnirRestClient("http://x", { fetch });
    const d = await c.discover();
    expect(captured.url).toBe("http://x/api/sleipnir/discovery");
    expect(captured.init.method).toBe("GET");
    expect(d.controllers).toEqual([]);
  });

  it("callBatch() POSTet nach /json/multi, setzt leere Ids und liefert Array", async () => {
    let captured: any;
    const fetch = vi.fn(async (url: string, init: any) => {
      captured = { url, init };
      return okResponse([
        { code: 200, data: "a", id: "C.M", isSuccess: true },
        { code: 200, data: "b", id: "C.M", isSuccess: true },
      ]);
    }) as unknown as FetchImpl;
    const c = new SleipnirRestClient("http://x", { fetch });
    const arr = await c.callBatch(
      [{ controller: "C", method: "M", params: [] } as any],
      ExecutionMode.Parallel,
    );
    expect(captured.url).toBe("http://x/api/sleipnir/json/multi");
    const body = JSON.parse(captured.init.body);
    expect(body.mode).toBe(ExecutionMode.Parallel);
    expect(body.requests[0].id).toBe("C.M"); // auto-gesetzt
    expect(arr).toHaveLength(2);
  });

  it("logischer Nicht-2xx-Code (in 200-Body): call liefert Response, callJson wirft SleipnirError", async () => {
    const fetch = vi.fn(async () =>
      okResponse({ code: 404, data: "not found", isSuccess: false, error: { code: 404, message: "not found" } }),
    ) as unknown as FetchImpl;
    const c = new SleipnirRestClient("http://x", { fetch });
    const r = await c.call("C", "M", {});
    expect(r.isSuccess).toBe(false); // call wirft nicht
    await expect(c.callJson("C", "M", {})).rejects.toBeInstanceOf(SleipnirError);
  });

  it("non-2xx HTTP -> synthetische Response; callBinary wirft SleipnirError", async () => {
    const fetch = vi.fn(async () => httpError(429, "rate limited")) as unknown as FetchImpl;
    const c = new SleipnirRestClient("http://x", { fetch });
    const r = await c.call("C", "M", {});
    expect(r.code).toBe(429);
    expect(r.isSuccess).toBe(false);
    expect(r.error?.code).toBe(429);
    await expect(c.callBinary("C", "M", {})).rejects.toMatchObject({ code: 429 });
  });

  it("Abbruch (aborted signal) -> CancelledError, nicht SleipnirError", async () => {
    const fetch = abortAwareFetch(() => okResponse({ code: 200, isSuccess: true }));
    const c = new SleipnirRestClient("http://x", { fetch });
    const ac = new AbortController();
    ac.abort();
    await expect(c.call("C", "M", {}, { signal: ac.signal })).rejects.toBeInstanceOf(
      CancelledError,
    );
  });

  it("Timeout -> CancelledError mit timedOut=true", async () => {
    const fetch = hangingFetch();
    const c = new SleipnirRestClient("http://x", { fetch, callTimeout: 25 });
    await expect(c.call("C", "M", {})).rejects.toMatchObject({
      name: "CancelledError",
      timedOut: true,
    });
  });

  it("callBinary dekodiert content (base64) -> Uint8Array", async () => {
    const fetch = vi.fn(async () =>
      okResponse({ code: 200, content: "AQID", isSuccess: true }), // base64 von [1,2,3]
    ) as unknown as FetchImpl;
    const c = new SleipnirRestClient("http://x", { fetch });
    const bytes = await c.callBinary("C", "M", {});
    expect(Array.from(bytes!)).toEqual([1, 2, 3]);
  });

  it("leitet isSuccess aus code ab, wenn der Server das Feld nicht sendet (Wire-Fakt)", async () => {
    // Der C#-Server serialisiert IsSuccess nicht ([JsonIgnore], aus code berechnet).
    // Der Client muss es daher aus code ableiten (200–299 => true).
    const fetch = vi.fn(async () =>
      okResponse({ code: 200, data: "ok", id: "C.M" }), // kein isSuccess-Feld!
    ) as unknown as FetchImpl;
    const c = new SleipnirRestClient("http://x", { fetch });
    const r = await c.call("C", "M", {});
    expect(r.isSuccess).toBe(true); // abgeleitet, nicht vom Server gesendet
    // callJson darf nicht werfen, weil isSuccess korrekt abgeleitet wurde:
    const v = await c.callJson<string>("C", "M", {});
    expect(v).toBe("ok");
  });

  it("leitet isSuccess=false bei non-2xx code ab (ohne Server-Feld)", async () => {
    const fetch = vi.fn(async () =>
      okResponse({
        code: 400,
        data: "bad",
        error: { code: 400, message: "bad" },
      }), // kein isSuccess-Feld
    ) as unknown as FetchImpl;
    const c = new SleipnirRestClient("http://x", { fetch });
    const r = await c.call("C", "M", {});
    expect(r.isSuccess).toBe(false);
    await expect(c.callJson("C", "M", {})).rejects.toBeInstanceOf(SleipnirError);
  });
});