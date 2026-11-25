import { describe, it, expect } from "vitest";
import { toCamelCase, shortName, pascalCase, normalizeTypeKey } from "../../src/core/casing.js";

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

describe("normalizeTypeKey (defensive: AQ CLR generic keys from older servers)", () => {
  const aq = (n: string) => `${n}, TicketSystem.Api, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`;

  it("leaves plain and already-flattened keys unchanged", () => {
    expect(normalizeTypeKey("MyApp.Order")).toBe("MyApp.Order");
    expect(normalizeTypeKey("MyApp.PageOfTicketDto")).toBe("MyApp.PageOfTicketDto");
  });

  it("flattens an assembly-qualified closed generic", () => {
    const key = `TicketSystem.Api.ProbeEnvelope\`1[[${aq("TicketSystem.Api.TicketDto")}]]`;
    expect(normalizeTypeKey(key)).toBe("TicketSystem.Api.ProbeEnvelopeOfTicketDto");
    expect(shortName(key)).toBe("ProbeEnvelopeOfTicketDto");
  });

  it("flattens multiple, nested and array arguments in declaration order", () => {
    const inner = `TicketSystem.Api.Page\`1[[System.Int32, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]`;
    const key = `TicketSystem.Api.Pair\`2[[${aq("TicketSystem.Api.TicketDto[]")}],[${aq(inner)}]]`;
    expect(normalizeTypeKey(key)).toBe("TicketSystem.Api.PairOfTicketDtoArrayAndPageOfInt32");
  });

  it("keeps only the own arguments of a nested generic", () => {
    const key = `MyApp.Outer\`1+Inner\`1[[${aq("MyApp.A")}],[${aq("MyApp.B")}]]`;
    expect(normalizeTypeKey(key)).toBe("MyApp.Outer+InnerOfB");
  });

  it("returns malformed input unchanged", () => {
    expect(normalizeTypeKey("MyApp.Page`1[[broken")).toBe("MyApp.Page`1[[broken");
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