import { describe, it, expect } from "vitest";
import {
  tsTypeOf, csTypeOf, pyTypeOf, defaultValueForType, isScalar, isVoidReturn,
} from "../../src/core/scalars.js";

describe("tsTypeOf", () => {
  it("maps numeric kinds to number", () => {
    expect(tsTypeOf("int")).toBe("number");
    expect(tsTypeOf("Int32")).toBe("number");
    expect(tsTypeOf("long")).toBe("number");
    expect(tsTypeOf("double")).toBe("number");
    expect(tsTypeOf("decimal")).toBe("number");
    expect(tsTypeOf("number")).toBe("number");
  });
  it("maps bool/boolean to boolean", () => {
    expect(tsTypeOf("bool")).toBe("boolean");
    expect(tsTypeOf("Boolean")).toBe("boolean");
  });
  it("maps string-like kinds to string", () => {
    expect(tsTypeOf("string")).toBe("string");
    expect(tsTypeOf("DateTime")).toBe("string");
    expect(tsTypeOf("Guid")).toBe("string");
    expect(tsTypeOf("DateTimeOffset")).toBe("string");
  });
  it("maps any-shaped kinds to unknown", () => {
    expect(tsTypeOf("object")).toBe("unknown");
    expect(tsTypeOf("JsonElement")).toBe("unknown");
    expect(tsTypeOf("Dictionary")).toBe("unknown");
  });
  it("falls back to the short name for complex types", () => {
    expect(tsTypeOf("MyApp.Order")).toBe("Order");
    expect(tsTypeOf("Order")).toBe("Order");
  });
});

describe("csTypeOf (Increment 2 home)", () => {
  it("maps scalars to their C# spelling", () => {
    expect(csTypeOf("int")).toBe("int");
    expect(csTypeOf("long")).toBe("long");
    expect(csTypeOf("bool")).toBe("bool");
    expect(csTypeOf("DateTime")).toBe("DateTime");
    expect(csTypeOf("Guid")).toBe("Guid");
  });
});

describe("pyTypeOf (Increment 2 home)", () => {
  it("maps scalars to Python", () => {
    expect(pyTypeOf("bool")).toBe("bool");
    expect(pyTypeOf("string")).toBe("str");
    expect(pyTypeOf("int")).toBe("int");
  });
});

describe("defaultValueForType (mirrors params.ts:12-21)", () => {
  it("defaults numbers to 0, bools to false, strings to ''", () => {
    expect(defaultValueForType("int")).toBe(0);
    expect(defaultValueForType("double")).toBe(0);
    expect(defaultValueForType("bool")).toBe(false);
    expect(defaultValueForType("string")).toBe("");
  });
  it("defaults complex types to null", () => {
    expect(defaultValueForType("Order")).toBeNull();
    expect(defaultValueForType("object")).toBeNull();
  });
});

describe("isScalar / isVoidReturn", () => {
  it("recognizes scalars", () => {
    expect(isScalar("int")).toBe(true);
    expect(isScalar("string")).toBe(true);
    expect(isScalar("bool")).toBe(true);
    expect(isScalar("Order")).toBe(false);
    expect(isScalar("object")).toBe(false);
  });
  it("recognizes void returns", () => {
    expect(isVoidReturn("void")).toBe(true);
    expect(isVoidReturn("Task")).toBe(true);
    expect(isVoidReturn("int")).toBe(false);
  });
});