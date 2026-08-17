import { describe, it, expect } from "vitest";
import { assertDiscoveryShape, DiscoveryShapeError } from "../../src/core/discovery.js";
import { readFixture } from "./fixture.js";

const V = "1";
const ok = (o: Record<string, unknown>) => ({ discoveryVersion: V, ...o });

describe("assertDiscoveryShape", () => {
  it("accepts a valid Story-01 payload", () => {
    const d = readFixture();
    expect(d.discoveryVersion).toBe(V);
    expect(d.controllers.length).toBe(6);
    expect(Object.keys(d.types).length).toBe(6);
  });

  it("rejects a non-object payload", () => {
    expect(() => assertDiscoveryShape(null)).toThrow(DiscoveryShapeError);
    expect(() => assertDiscoveryShape("hello")).toThrow(DiscoveryShapeError);
    expect(() => assertDiscoveryShape(42)).toThrow(DiscoveryShapeError);
  });

  // --- discoveryVersion (additive-only no-drift gate) -----------------------

  it("rejects a payload missing discoveryVersion", () => {
    expect(() => assertDiscoveryShape({ controllers: [], types: {} })).toThrow(/discoveryVersion/);
  });
  it("rejects an unknown discoveryVersion loudly", () => {
    expect(() => assertDiscoveryShape({ discoveryVersion: "99", controllers: [], types: {} }))
      .toThrow(/Unsupported discoveryVersion/);
  });

  // --- envelope shape -------------------------------------------------------

  it("rejects a payload missing the controllers array", () => {
    expect(() => assertDiscoveryShape(ok({ types: {} }))).toThrow(/controllers/);
  });
  it("rejects a payload missing the types object", () => {
    expect(() => assertDiscoveryShape(ok({ controllers: [] }))).toThrow(/types/);
  });
  it("rejects a payload where types is an array, not an object", () => {
    expect(() => assertDiscoveryShape(ok({ controllers: [], types: [] }))).toThrow(/types/);
  });
  it("rejects a controller entry missing a string name", () => {
    expect(() => assertDiscoveryShape(ok({ controllers: [{}], types: {} }))).toThrow(/name/);
  });
  it("rejects a controller missing its methods array", () => {
    expect(() => assertDiscoveryShape(ok({ controllers: [{ name: "X" }], types: {} }))).toThrow(/methods/);
  });

  // --- TypeRef validation ----------------------------------------------------

  it("rejects a method with a non-TypeRef returnType", () => {
    const payload = ok({
      controllers: [{ name: "C", methods: [{ methodName: "M", returnType: "int", parameters: [] }] }],
      types: {},
    });
    expect(() => assertDiscoveryShape(payload)).toThrow(/returnType is not a TypeRef/);
  });
  it("rejects an invalid TypeRef kind", () => {
    const payload = ok({
      controllers: [{ name: "C", methods: [{ methodName: "M", returnType: { kind: "tuple" }, parameters: [] }] }],
      types: {},
    });
    expect(() => assertDiscoveryShape(payload)).toThrow(/invalid kind/);
  });
  it("rejects an invalid scalar name", () => {
    const payload = ok({
      controllers: [{ name: "C", methods: [{ methodName: "M", returnType: { kind: "scalar", name: "widget" }, parameters: [] }] }],
      types: {},
    });
    expect(() => assertDiscoveryShape(payload)).toThrow(/invalid scalar name/);
  });
  it("rejects an array missing its element", () => {
    const payload = ok({
      controllers: [{ name: "C", methods: [{ methodName: "M", returnType: { kind: "array" }, parameters: [] }] }],
      types: {},
    });
    expect(() => assertDiscoveryShape(payload)).toThrow(/missing "element"/);
  });
  it("rejects a map missing key or value", () => {
    const payload = ok({
      controllers: [{ name: "C", methods: [{ methodName: "M", returnType: { kind: "map", value: { kind: "scalar", name: "int" } }, parameters: [] }] }],
      types: {},
    });
    expect(() => assertDiscoveryShape(payload)).toThrow(/missing "key" or "value"/);
  });
  it("rejects a ref that does not resolve into the types registry", () => {
    const payload = ok({
      controllers: [{ name: "C", methods: [{ methodName: "M", returnType: { kind: "ref", ref: "Nope" }, parameters: [] }] }],
      types: {},
    });
    expect(() => assertDiscoveryShape(payload)).toThrow(/does not resolve into the types registry/);
  });
  it("accepts a ref that resolves into the types registry", () => {
    const payload = ok({
      controllers: [{ name: "C", methods: [{ methodName: "M", returnType: { kind: "ref", ref: "T" }, parameters: [] }] }],
      types: { T: { kind: "object", typeName: "T", properties: [] } },
    });
    expect(() => assertDiscoveryShape(payload)).not.toThrow();
  });

  // --- types registry validation --------------------------------------------

  it("rejects a type registry entry with an invalid kind", () => {
    const payload = ok({ controllers: [], types: { T: { kind: "union", properties: [] } } });
    expect(() => assertDiscoveryShape(payload)).toThrow(/invalid kind/);
  });
  it("rejects an object type missing its properties array", () => {
    const payload = ok({ controllers: [], types: { T: { kind: "object" } } });
    expect(() => assertDiscoveryShape(payload)).toThrow(/missing a "properties" array/);
  });
  it("rejects an enum type with empty members", () => {
    const payload = ok({ controllers: [], types: { T: { kind: "enum", members: [] } } });
    expect(() => assertDiscoveryShape(payload)).toThrow(/non-empty "members"/);
  });
  it("accepts a valid enum type with members", () => {
    const payload = ok({
      controllers: [{ name: "C", methods: [{ methodName: "M", returnType: { kind: "ref", ref: "P" }, parameters: [] }] }],
      types: { P: { kind: "enum", typeName: "P", members: [{ name: "Low", value: 0 }] } },
    });
    expect(() => assertDiscoveryShape(payload)).not.toThrow();
  });
});