import { describe, it, expect } from "vitest";
import { NamingResolver } from "../../src/core/naming.js";

describe("NamingResolver", () => {
  it("emits the short name when there is no collision", () => {
    const r = new NamingResolver();
    r.register("MyApp.Order");
    expect(r.resolve("MyApp.Order")).toBe("Order");
  });

  it("is idempotent", () => {
    const r = new NamingResolver();
    r.register("MyApp.Order");
    r.register("MyApp.Order");
    expect(r.resolve("MyApp.Order")).toBe("Order");
  });

  it("prefixes the parent segment on a short-name collision", () => {
    const r = new NamingResolver();
    r.register("Foo.Order");
    r.register("Bar.Order");
    expect(r.resolve("Foo.Order")).toBe("FooOrder");
    expect(r.resolve("Bar.Order")).toBe("BarOrder");
  });

  it("walks further up when the parent prefix still collides", () => {
    const r = new NamingResolver();
    r.register("A.Foo.Order");
    r.register("B.Foo.Order");
    expect(r.resolve("A.Foo.Order")).toBe("AFooOrder");
    expect(r.resolve("B.Foo.Order")).toBe("BFooOrder");
  });

  it("Story-01 has no collisions, so names stay short", () => {
    const r = new NamingResolver();
    for (const t of [
      "TrameStories.Story01.Order",
      "TrameStories.Story01.Customer",
      "TrameStories.Story01.OrderLine",
    ]) r.register(t);
    expect(r.resolve("TrameStories.Story01.Order")).toBe("Order");
    expect(r.resolve("TrameStories.Story01.Customer")).toBe("Customer");
  });
});