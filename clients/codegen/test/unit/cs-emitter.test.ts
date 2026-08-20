import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitCsClient } from "../../src/emitters/cs.js";
import { readFixture } from "./fixture.js";

const here = dirname(fileURLToPath(import.meta.url));
const snapshotPath = join(here, "..", "snapshots", "story01.cs", "SleipnirGenerated.cs");

describe("emitCsClient (golden against story01 snapshot)", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());
  const tree = emitCsClient(input);

  it("emits a single SleipnirGenerated.cs file", () => {
    expect(Object.keys(tree).sort()).toEqual(["SleipnirGenerated.cs"]);
  });

  it("matches the committed snapshot byte-for-byte", () => {
    const snapshot = readFileSync(snapshotPath, "utf8");
    expect(tree["SleipnirGenerated.cs"]).toBe(snapshot);
  });

  it("emits camelCase [JsonPropertyName] on nullable PascalCase POCOs", () => {
    const cs = tree["SleipnirGenerated.cs"];
    expect(cs).toContain('[JsonPropertyName("customerId")]\n        public int? CustomerId { get; set; }');
    expect(cs).toContain('[JsonPropertyName("placedAt")]\n        public DateTime? PlacedAt { get; set; }');
    // Arrays are nullable List<T>.
    expect(cs).toContain("public class OrderLine\n");
  });

  it("emits Arg<T> params and the Alias/Arg/Call/Batch runtime", () => {
    const cs = tree["SleipnirGenerated.cs"];
    expect(cs).toContain("public Call GetById(Arg<int> id) => new Call(SleipnirCall.Init(\"Order\", \"GetById\").Param(\"id\", id.ToWireValue()));");
    expect(cs).toContain("public Call GetByIds(Arg<List<int>> articleIds) =>");
    expect(cs).toContain("public readonly struct Alias");
    expect(cs).toContain("public readonly struct Arg<T>");
    expect(cs).toContain("internal object? ToWireValue() => _value is Alias a ? a.Placeholder : _value;");
    expect(cs).toContain("public BatchEntry Add(Call call)");
    expect(cs).toContain("Mode = ExecutionMode.Serial");
  });

  it("exposes strips the leading @ for the dependencyMapping key", () => {
    const cs = tree["SleipnirGenerated.cs"];
    expect(cs).toContain("_call.Exposes(jsonPath, alias.StartsWith('@') ? alias.Substring(1) : alias);");
  });

  it("alias ensures the leading @ for the placeholder (both call styles)", () => {
    const cs = tree["SleipnirGenerated.cs"];
    // Symmetric to exposes: Alias("ids") → "@ids" and Alias("@ids") → "@ids".
    // The 1.2.1 bug was `new(name)` — returned the bare name on the wire.
    expect(cs).toContain("public Alias Alias(string name) => new(name.StartsWith('@') ? name : \"@\" + name);");
  });

  it("emits the root SleipnirGeneratedClient with Call<T> + Subscribe<T> + Batch + per-controller accessors", () => {
    const cs = tree["SleipnirGenerated.cs"];
    expect(cs).toContain("public Task<T?> Call<T>(Call call) => _client.Call<T>(call.ToRequest());");
    expect(cs).toContain("public Task<SleipnirMultiCallResponse> Batch(Batch batch) =>");
    expect(cs).toContain("public OrderClient Order { get; } = new();");
    // (string baseUrl) wraps a SleipnirTransportRouter with the codegen capability (default all).
    expect(cs).toContain("new SleipnirTransportRouter(");
    expect(cs).toContain("SleipnirRouterOptions { BaseUrl = baseUrl, Capability = SleipnirBundleCapability.All }");
    // Event entry point routes through the router to the active event backend.
    expect(cs).toContain("public Task<SleipnirSubscription<T>> Subscribe<T>(Call call, ResumePolicy? resumePolicy = null, CancellationToken ct = default)");
    // A custom ISleipnirClient overload remains (tests / pre-configured backends).
    expect(cs).toContain("public SleipnirGeneratedClient(ISleipnirClient client) => _client = client;");
    // Named SleipnirGeneratedClient to avoid the global SleipnirClient namespace collision.
    expect(cs).not.toContain("public sealed class SleipnirClient\n");
  });
});