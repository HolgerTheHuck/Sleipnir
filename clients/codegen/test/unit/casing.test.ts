import { describe, it, expect } from "vitest";
import { toCamelCase, shortName, pascalCase } from "../../src/core/casing.js";

describe("toCamelCase (mirrors System.Text.Json CamelCase)", () => {
  it("lowercases a single leading uppercase char", () => {
    expect(toCamelCase("Id")).toBe("id");
    expect(toCamelCase("Name")).toBe("name");
    expect(toCamelCase("OrderLine")).toBe("orderLine");
  });

  it("lowercases an all-uppercase acronym", () => {
    expect(toCamelCase("ID")).toBe("id");
    expect(toCamelCase("URL")).toBe("url");
  });

  it("keeps the last upper char when an acronym precedes a lowercase word", () => {
    expect(toCamelCase("IPAddress")).toBe("ipAddress");
    expect(toCamelCase("URLPath")).toBe("urlPath");
  });

  it("leaves already-camelCase names unchanged", () => {
    expect(toCamelCase("order")).toBe("order");
    expect(toCamelCase("customerId")).toBe("customerId");
  });

  it("handles empty strings", () => {
    expect(toCamelCase("")).toBe("");
  });
});

describe("shortName", () => {
  it("returns the last dot-segment", () => {
    expect(shortName("MyApp.Foo.Order")).toBe("Order");
    expect(shortName("Order")).toBe("Order");
  });
});

describe("pascalCase", () => {
  it("capitalizes the first char", () => {
    expect(pascalCase("order")).toBe("Order");
    expect(pascalCase("orderLine")).toBe("OrderLine");
  });
  it("leaves already-PascalCase names readable", () => {
    expect(pascalCase("Order")).toBe("Order");
    expect(pascalCase("IPAddress")).toBe("IPAddress");
  });
});