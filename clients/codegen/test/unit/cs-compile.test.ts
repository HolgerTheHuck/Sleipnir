// Regression gate: the generated C# client (TrameGenerated.cs) must compile
// against the real TrameClient runtime, AND the typed batch must build the
// Story-01 diamond (producer exposes camelCase paths, consumer resolves the
// alias via Arg<T> + Alias). Spawns `dotnet build` against a temp project under
// the package root that ProjectReferences TrameClient.csproj. Skipped when
// `dotnet` is not on PATH so the suite stays green on non-.NET machines.
import { describe, it, expect } from "vitest";
import { mkdirSync, writeFileSync, rmSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { spawnSync } from "node:child_process";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitCsClient } from "../../src/emitters/cs.js";
import { readFixture } from "./fixture.js";

const here = dirname(fileURLToPath(import.meta.url));
const pkgRoot = join(here, "..", "..");
const compileDir = join(pkgRoot, ".cs-compile");

/** Probe `dotnet --version`; return the binary name if available, else null. */
function findDotnet(): string | null {
  for (const bin of ["dotnet"]) {
    const r = spawnSync(bin, ["--version"], { encoding: "utf8", shell: process.platform === "win32" });
    if (r.status === 0) return bin;
  }
  return null;
}

const dotnet = findDotnet();
const testFn = dotnet ? it : it.skip;

// A harness that builds the Story-01 diamond via the GENERATED C# client +
// Batch (compile-only; Main is never executed by `dotnet build`). Exercises:
//  - single typed Call<T>
//  - Arg<T> literal (42) + Arg<T> from Alias (o.Alias("@customerId"))
//  - array alias (lines.Alias("@articleIds") → Arg<List<int>>)
//  - TrameMultiCallResponse.Get<T> for scalar, object, and list results
const harness = `using System.Collections.Generic;
using System.Threading.Tasks;
using Trame.Generated;

public static class Program
{
    public static async Task Main()
    {
        var client = new TrameGeneratedClient("http://localhost:5001");

        // Single typed call: Call<T> deserializes into the POCO.
        var order = await client.Call<Order>(client.Order.GetById(42));

        // Typed diamond batch (Serial — required for @alias resolution).
        var batch = new Batch();
        var o = batch.Add(client.Order.GetById(42))
            .Exposes("$.customerId", "@customerId")
            .Exposes("$.id", "@orderId")
            .Exposes("$.shippingAddressId", "@addressId");
        batch.Add(client.Customer.GetById(o.Alias("@customerId")));
        var lines = batch.Add(client.OrderLine.GetByOrder(o.Alias("@orderId")))
            .Exposes("$[*].articleId", "@articleIds");
        batch.Add(client.Article.GetByIds(lines.Alias("@articleIds")));
        batch.Add(client.Stock.GetByArticles(lines.Alias("@articleIds")));
        batch.Add(client.Address.GetById(o.Alias("@addressId")));

        var resp = await client.Batch(batch);
        // Fetch results by request id (topological order is not request order).
        var fetchedOrder = resp.Get<Order>("Order.GetById");
        var customer = resp.Get<Customer>("Customer.GetById");
        var fetchedLines = resp.Get<List<OrderLine>>("OrderLine.GetByOrder");
        var articles = resp.Get<List<Article>>("Article.GetByIds");
    }
}
`;

// net8.0 console app that references the real Trame.Client runtime. Path:
// .cs-compile → codegen → clients → Trame → TrameClient.
const csproj = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <RootNamespace>TrameCompileGate</RootNamespace>
    <AssemblyName>TrameCompileGate</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\\..\\..\\TrameClient\\TrameClient.csproj" />
  </ItemGroup>
</Project>
`;

describe(dotnet ? "generated C# compiles + typed diamond builds (dotnet build)"
  : "generated C# compiles (skipped: dotnet not on PATH)", () => {
  testFn("dotnet build exits 0 against the diamond harness", () => {
    rmSync(compileDir, { recursive: true, force: true });
    mkdirSync(compileDir, { recursive: true });

    const tree = emitCsClient(buildEmitterInput(readFixture(), new NamingResolver()));
    for (const [path, content] of Object.entries(tree)) {
      writeFileSync(join(compileDir, path), content, "utf8");
    }
    writeFileSync(join(compileDir, "Harness.cs"), harness, "utf8");
    writeFileSync(join(compileDir, "CsCompile.csproj"), csproj, "utf8");

    const r = spawnSync(dotnet!, ["build", join(compileDir, "CsCompile.csproj"), "-c", "Release", "--nologo", "-clp:NoSummary"], {
      encoding: "utf8",
      cwd: pkgRoot,
      shell: process.platform === "win32",
      timeout: 180_000,
    });

    if ((r.status ?? 1) !== 0) {
      console.error("dotnet build stdout:\n" + r.stdout);
      console.error("dotnet build stderr:\n" + r.stderr);
    }
    expect(r.status, `dotnet build failed:\n${r.stdout}\n${r.stderr}`).toBe(0);

    rmSync(compileDir, { recursive: true, force: true });
  }, { timeout: 200_000 });
});