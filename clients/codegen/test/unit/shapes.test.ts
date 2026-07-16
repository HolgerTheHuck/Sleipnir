import { describe, it, expect } from "vitest";
import type { TypeRef } from "trame-client";
import {
  shapeFromRef, returnShape, paramShape, lookupTypeMeta, findProperty,
} from "../../src/core/shapes.js";
import { readFixture } from "./fixture.js";

const discovery = readFixture();

const scalar = (name: string): TypeRef => ({ kind: "scalar", name });
const ref = (r: string, nullable = false): TypeRef => ({ kind: "ref", ref: r, ...(nullable ? { nullable: true } : {}) });
const array = (el: TypeRef): TypeRef => ({ kind: "array", element: el });
const map = (k: TypeRef, v: TypeRef): TypeRef => ({ kind: "map", key: k, value: v });
const opaque = (n: string): TypeRef => ({ kind: "opaque", nativeName: n });

describe("shapeFromRef", () => {
  it("maps scalars to their JSON kind", () => {
    expect(shapeFromRef(scalar("int"), null).kind).toBe("number");
    expect(shapeFromRef(scalar("bool"), null).kind).toBe("boolean");
    expect(shapeFromRef(scalar("string"), null).kind).toBe("string");
    expect(shapeFromRef(scalar("datetime"), null).kind).toBe("string");
  });
  it("maps bytes to string (base64 on the wire)", () => {
    expect(shapeFromRef(scalar("bytes"), null).kind).toBe("string");
  });
  it("maps any-shaped scalars to unknown + acceptsAny", () => {
    const s = shapeFromRef(scalar("any"), null);
    expect(s.kind).toBe("unknown");
    expect(s.acceptsAny).toBe(true);
  });
  it("resolves an object ref to object + typeMeta", () => {
    const s = shapeFromRef(ref("TrameStories.Story01.Order"), discovery);
    expect(s.kind).toBe("object");
    expect(s.typeMeta).toBeTruthy();
  });
  it("maps opaque to unknown + acceptsAny", () => {
    const s = shapeFromRef(opaque("TrameResponse"), null);
    expect(s.kind).toBe("unknown");
    expect(s.acceptsAny).toBe(true);
  });
  it("maps map to object + acceptsAny (JSON object of dynamic keys)", () => {
    const s = shapeFromRef(map(scalar("string"), scalar("int")), null);
    expect(s.kind).toBe("object");
    expect(s.acceptsAny).toBe(true);
  });
  it("unwraps array/set/stream element shape", () => {
    expect(shapeFromRef(array(scalar("int")), null).kind).toBe("array");
    expect(shapeFromRef(array(scalar("int")), null).element?.kind).toBe("number");
    expect(shapeFromRef({ kind: "set", element: scalar("int") }, null).kind).toBe("array");
    expect(shapeFromRef({ kind: "stream", element: scalar("int") }, null).kind).toBe("array");
  });
  it("falls back to unknown for an unresolved ref", () => {
    expect(shapeFromRef(ref("NotAType"), null).kind).toBe("unknown");
  });
});

describe("returnShape", () => {
  it("unwraps List<T> (array ref) to an array of element shape", () => {
    const m = discovery.controllers.flatMap((c) => c.methods).find((m) => m.methodName === "GetByArticles")!;
    const s = returnShape(m, discovery);
    expect(s?.kind).toBe("array");
    expect(s?.element?.kind).toBe("object");
  });
  it("returns null for void", () => {
    const m = { methodName: "Do", returnType: { kind: "void" }, parameters: [] } as never;
    expect(returnShape(m as never, discovery)).toBeNull();
  });
});

describe("paramShape", () => {
  it("unwraps List<int> (array scalar) to an array of number", () => {
    const m = discovery.controllers.flatMap((c) => c.methods).find((m) => m.methodName === "GetByArticles")!;
    const p = m.parameters.find((p) => p.parameterName === "articleIds")!;
    const s = paramShape(p, discovery);
    expect(s.kind).toBe("array");
    expect(s.element?.kind).toBe("number");
  });
});

describe("lookupTypeMeta + findProperty (camelCase wire match)", () => {
  it("finds a registered type by its registry key", () => {
    const tm = lookupTypeMeta(discovery, "TrameStories.Story01.Order");
    expect(tm?.properties.length).toBeGreaterThan(0);
  });
  it("finds a property by its camelCase wire name, not PascalCase", () => {
    const tm = lookupTypeMeta(discovery, "TrameStories.Story01.Order")!;
    expect(findProperty(tm, "customerId")).toBeDefined();   // camelCase wire name → found
    expect(findProperty(tm, "CustomerId")).toBeUndefined(); // PascalCase → not on the wire
  });
});