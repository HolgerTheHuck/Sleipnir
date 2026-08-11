import { describe, it, expect } from "vitest";
import { SleipnirCall } from "../../src/fluent.js";
import { toBase64 } from "../../src/request.js";
import { ExecutionMode } from "../../src/types.js";

describe("SleipnirCall", () => {
  it("named + named(id) + exposes -> korrekter SleipnirRequest", () => {
    const req = SleipnirCall.init("Customer", "Add")
      .with({ name: "Alice" })
      .named("step1")
      .exposes("$", "newId")
      .toRequest();
    expect(req.controller).toBe("Customer");
    expect(req.method).toBe("Add");
    expect(req.id).toBe("step1");
    expect(req.dependencyMapping).toEqual({ newId: "$" });
    expect(req.binaryData).toBeNull();
    expect(req.params).toEqual([
      { parameterName: "name", data: "Alice", num: 0 },
    ]);
  });

  it("default id = controller.method", () => {
    expect(SleipnirCall.init("C", "M").toRequest().id).toBe("C.M");
  });

  it("positional via array -> param{i} + num", () => {
    const req = SleipnirCall.init("C", "M").with([1, 2]).toRequest();
    expect(req.params).toEqual([
      { parameterName: "param0", data: 1, num: 0 },
      { parameterName: "param1", data: 2, num: 1 },
    ]);
  });

  it("param() fügt benannten Parameter hinzu", () => {
    const req = SleipnirCall.init("C", "M").param("id", 42).toRequest();
    expect(req.params).toEqual([
      { parameterName: "id", data: 42, num: 0 },
    ]);
  });

  it("withAlias(@x) fügt Platzhalter-Parameter hinzu", () => {
    const req = SleipnirCall.init("C", "M")
      .with(["base"])
      .withAlias("@newId")
      .toRequest();
    const arr = req.params!;
    expect(arr[1]).toEqual({ parameterName: "newId", data: "@newId", num: 1 });
  });

  it("withBinary kodiert als base64", () => {
    const bytes = new Uint8Array([9]);
    const req = SleipnirCall.init("C", "M").withBinary(bytes).toRequest();
    expect(req.binaryData).toBe(toBase64(bytes));
  });

  it("leeres exposes -> dependencyMapping null", () => {
    expect(SleipnirCall.init("C", "M").toRequest().dependencyMapping).toBeNull();
  });

  it("batch() baut SleipnirMultiRequest mit mode", () => {
    const r = SleipnirCall.init("C", "M").toRequest();
    const m = SleipnirCall.batch([r], ExecutionMode.Serial);
    expect(m.mode).toBe(ExecutionMode.Serial);
    expect(m.requests).toHaveLength(1);
  });
});