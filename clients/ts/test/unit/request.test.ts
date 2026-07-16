import { describe, it, expect } from "vitest";
import {
  buildParams,
  buildSingle,
  buildMulti,
  toBase64,
  fromBase64,
} from "../../src/request.js";
import { ExecutionMode } from "../../src/types.js";

describe("buildParams", () => {
  it("named params -> [{parameterName, data, num}] (camelCase)", () => {
    const arr = buildParams({ id: 42, name: "Alice" });
    expect(arr).toEqual([
      { parameterName: "id", data: 42, num: 0 },
      { parameterName: "name", data: "Alice", num: 1 },
    ]);
  });

  it("positional params -> param{i} + num", () => {
    const arr = buildParams([42, "x"]);
    expect(arr).toEqual([
      { parameterName: "param0", data: 42, num: 0 },
      { parameterName: "param1", data: "x", num: 1 },
    ]);
  });

  it("undefined -> []", () => {
    expect(buildParams(undefined)).toEqual([]);
  });

  it("bettes verschachtelte Objekte als native Werte in data ein", () => {
    const arr = buildParams({ filter: { q: "a", n: 3 } });
    expect(arr[0].parameterName).toBe("filter");
    expect(arr[0].data).toEqual({ q: "a", n: 3 });
  });
});

describe("base64 (isomorph)", () => {
  it("round-trips bytes incl. multi-byte UTF-8", () => {
    const bytes = new TextEncoder().encode("héllo🎉");
    const back = fromBase64(toBase64(bytes));
    expect(new TextDecoder().decode(back)).toBe("héllo🎉");
  });

  it("round-trips empty", () => {
    expect(toBase64(new Uint8Array(0))).toBe("");
    expect(fromBase64("").length).toBe(0);
  });
});

describe("buildSingle", () => {
  it("defaults id to controller.method and nulls dependencyMapping", () => {
    const req = buildSingle({ controller: "C", method: "M", params: { id: 1 } });
    expect(req.id).toBe("C.M");
    expect(req.dependencyMapping).toBeNull();
    expect(req.binaryData).toBeNull();
  });

  it("encodes Uint8Array binary to base64", () => {
    const req = buildSingle({
      controller: "C",
      method: "M",
      binaryData: new Uint8Array([1, 2, 3]),
    });
    expect(req.binaryData).toBe(toBase64(new Uint8Array([1, 2, 3])));
  });
});

describe("buildMulti", () => {
  it("sets mode", () => {
    expect(buildMulti([], ExecutionMode.Serial).mode).toBe(ExecutionMode.Serial);
    expect(buildMulti([], ExecutionMode.Parallel).mode).toBe(ExecutionMode.Parallel);
  });
});