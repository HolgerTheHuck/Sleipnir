import { describe, it, expect } from "vitest";
import { SleipnirError, CancelledError, isCancelled } from "../../src/errors.js";

describe("SleipnirError", () => {
  it("fromBody übernimmt code/message/requestId", () => {
    const e = SleipnirError.fromBody({ code: 404, message: "not found", requestId: "x" });
    expect(e).toBeInstanceOf(SleipnirError);
    expect(e).toBeInstanceOf(Error);
    expect(e.code).toBe(404);
    expect(e.message).toBe("not found");
    expect(e.requestId).toBe("x");
  });

  it("fromResponse nutzt Fallback-Message, wenn kein error-Feld (Data trägt seit Single-Pass-Fix keine Fehlertexte)", () => {
    const e = SleipnirError.fromResponse({ code: 500, isSuccess: false });
    expect(e.code).toBe(500);
    expect(e.message).toBe("Sleipnir call failed with code 500.");
  });

  it("fromResponse bevorzugt strukturiertes error-Feld", () => {
    const e = SleipnirError.fromResponse({
      code: 400,
      data: "x",
      isSuccess: false,
      error: { code: 400, message: "bad input" },
    });
    expect(e.message).toBe("bad input");
    expect(e.code).toBe(400);
  });

  it("fromResponse mit Fallback-Message bei leerem data", () => {
    const e = SleipnirError.fromResponse({ code: 403, data: null, isSuccess: false });
    expect(e.message).toContain("403");
  });
});

describe("CancelledError", () => {
  it("ist keine SleipnirError (unverpackt)", () => {
    const c = new CancelledError("x", true);
    expect(c).toBeInstanceOf(CancelledError);
    expect(c).not.toBeInstanceOf(SleipnirError);
    expect(c.timedOut).toBe(true);
    expect(isCancelled(c)).toBe(true);
  });

  it("isCancelled erkennt fetch-AbortError", () => {
    const abort = Object.assign(new Error("aborted"), { name: "AbortError" });
    expect(isCancelled(abort)).toBe(true);
  });
});