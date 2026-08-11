# Sleipnir Codegen — Build-Time Contract Onboarding

> The hands-on guide to Sleipnir's **.NET-native, Node-free** client-generation build action:
> the server-side **export + drift-check** (`Sleipnir.Server.Codegen`) and the client-side
> **Roslyn source generator** (`Sleipnir.Generator`). Together they turn the Sleipnir discovery JSON
> into a compile-time contract: the server *exports* it, the client is *generated* from it, and
> **any drift fails the build**. This is gRPC's real edge — a typed client + a build that breaks
> when the contract changes — without a second protocol, without `.proto`, without Node.

This document is the onboarding. For the *why* and the design history, see
[`CLIENT_GENERATION.md`](CLIENT_GENERATION.md). For the discovery schema itself, see
[`docs/discovery-schema.md`](docs/discovery-schema.md).

---

## TL;DR — the contract loop

```
   SERVER project                         CLIENT project
   ─────────────                          ──────────────
   [SleipnirController] C# classes    ──┐
   are the contract                 │  contract.sleipnir.json
                                   │  (the committed contract)
   Sleipnir.Server.Codegen             │
   regenerates discovery  ──────────┤
   from the built assembly          │        Sleipnir.Generator reads it
   at every build                   │        via AdditionalFiles and
                                   │        emits SleipnirGenerated.cs
   drift? ──► build FAILS           │        into the compilation
   (committed ≠ runtime)            │
   SLEIPNIR_REGEN_GOLDEN=1 ──► rewrite ┘
```

1. **Server**: the C# controllers *are* the contract. `Sleipnir.Server.Codegen` regenerates
   `contract.sleipnir.json` from the built assembly on every build and **fails the build if the
   committed file has drifted** from what the runtime actually exposes.
2. **Commit**: `contract.sleipnir.json` is checked in. It is the versioned, reviewable contract
   artifact — the thing a PR diff shows.
3. **Client**: `Sleipnir.Generator` reads that same `contract.sleipnir.json` and emits a typed
   `SleipnirGeneratedClient` **into the compilation**. No runtime codegen, no Node, no `sleipnir-gen`
   step in your build — the contract is consumed at compile time by Roslyn.

The discovery JSON is the single source of truth on both sides. The schema is versioned
(`discoveryVersion`, additive-only); a payload the generator/export tool does not understand is
rejected loudly (`SLEIPNIR001`), never silently degraded.

---

## Prerequisites

- .NET 8 SDK.
- A Sleipnir server (controllers decorated `[SleipnirController]` / `[SleipnirMethod]`).
- For the client, `Sleipnir.Client` (the runtime the generated stubs call).

The two packages this document covers:

| Package (NuGet `PackageId`) | TFM | Role | Imported build file |
|---|---|---|---|
| `Sleipnir.Server.Codegen` | net8.0 | Export tool + drift-check MSBuild target (server side) | `build/Sleipnir.Server.Codegen.targets` |
| `Sleipnir.Generator` | netstandard2.0 | Roslyn `IIncrementalGenerator` (client side) | `build/Sleipnir.Generator.props` |

Both are **self-contained**: `Sleipnir.Server.Codegen` ships the tool + its deps in `tasks/net8.0/`;
`Sleipnir.Generator` links its emission core into the generator assembly (the Roslyn analyzer load
context cannot resolve a `ProjectReference` dep, so the core is compiled *in*, not referenced).
Neither needs Node, `npm`, or `sleipnir-gen` on the build machine.

---

## 1. Server side — export + drift-check

### 1.1 Wire it

Reference the package and let the convention work — a file named `contract.sleipnir.json` in the
project directory is picked up automatically:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Sleipnir.Server.Codegen" Version="1.0.0" />
  </ItemGroup>
</Project>
```

That's it. `build/Sleipnir.Server.Codegen.targets` is auto-imported on `PackageReference`; it runs a
target `AfterTargets="Build"` that regenerates the discovery in-process and drift-checks it.

### 1.2 Generate the contract the first time

On the first build there is no committed contract yet, so the export tool **writes one** and exits
clean (exit 0). Commit it:

```bash
dotnet build                              # creates contract.sleipnir.json
git add contract.sleipnir.json
git commit -m "chore: add Sleipnir contract"
```

From now on every `dotnet build` regenerates the discovery from the built assembly and compares it
to the committed `contract.sleipnir.json`. They match → build succeeds. They differ → **build fails**.

### 1.3 The intended-change flow (regen)

When you intentionally change a controller (add/rename/remove a method, change a parameter type),
the committed contract is now stale and the build fails with a clear drift message plus a
normalized regenerated-vs-committed diff. Regenerate:

```bash
SLEIPNIR_REGEN_GOLDEN=1 dotnet build          # rewrites contract.sleipnir.json, build succeeds
git add contract.sleipnir.json
git commit -m "feat: rename Order.GetById -> Order.Get"
```

On Windows PowerShell: `$env:SLEIPNIR_REGEN_GOLDEN=1; dotnet build`. On cmd: `set SLEIPNIR_REGEN_GOLDEN=1 && dotnet build`.

The regen flow is the **only** way the committed contract changes. Drift without regen is a hard
build failure — that is the whole point (the `wsdl.exe` trap: a stale contract that lies).

### 1.4 What the target does, exactly

`SleipnirExportDriftCheck` runs `AfterTargets="Build"`, only when a contract file is configured
(`$(SleipnirContractFile)` is set, or `contract.sleipnir.json` exists in the project dir). It shells out
to the export tool in its **own process**:

```
dotnet <tool.dll> --assembly <TargetPath> --contract <SleipnirContractFile> [--regen]
```

Running in a separate process isolates the server-assembly load from MSBuild (no version
collisions, no locked files). The target only translates the tool's exit code into an MSBuild
`<Error>`; the tool's stdout (pass / drift diff / regen confirmation) is echoed into the build log.

| Exit code | Meaning | Build result |
|---|---|---|
| `0` | ok, or regenerated (with `--regen`) | success |
| `1` | **drift detected** | build fails (drift `<Error>`) |
| `2` | tool error (not a drift) | build fails (tool-error `<Error>`) |

### 1.5 Configuration (server)

| Property / env | Default | Purpose |
|---|---|---|
| `SleipnirContractFile` | `contract.sleipnir.json` if it exists in the project dir | Path to the contract (project-relative or absolute). Set explicitly to use a different name/location. |
| `SleipnirContractEnabled` | `true` | Set `false` to opt the project out (e.g. a library that transitively references the package but is not a server). |
| `SleipnirExportToolDll` | `$(MSBuildThisFileDirectory)..\tasks\net8.0\Sleipnir.Server.Codegen.dll` | Path to the export tool dll. The NuGet default is right for published consumption; for in-repo `ProjectReference` dev, set it to the built tool output (see §4). |
| `SLEIPNIR_REGEN_GOLDEN` (env) | unset | Set to `1` to regenerate the committed contract instead of failing on drift. |

---

## 2. Client side — the Roslyn source generator

### 2.1 Wire it

Reference the generator (as an analyzer) and the Sleipnir client runtime, and drop the server's
`contract.sleipnir.json` into the client project:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- The runtime the generated stubs call (SleipnirRestJsonClient, ISleipnirClient, SleipnirCall, ...). -->
    <PackageReference Include="Sleipnir.Client" Version="1.0.0" />
    <!-- The source generator: loaded as an analyzer, emits SleipnirGenerated.cs at compile time. -->
    <PackageReference Include="Sleipnir.Generator" Version="1.0.0"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
  <ItemGroup>
    <!-- The committed contract (copy it from the server project, or share it via a linked file). -->
    <AdditionalFiles Include="contract.sleipnir.json" />
  </ItemGroup>
</Project>
```

`build/Sleipnir.Generator.props` (auto-imported) already marks `contract.sleipnir.json` as an
`AdditionalFile` and surfaces `SleipnirContractFile` to the generator, so the explicit
`<AdditionalFiles Include="contract.sleipnir.json" />` is only needed when you place the contract
somewhere other than the project directory.

### 2.2 What you get

At compile time the generator emits `SleipnirGenerated.cs` in the `Sleipnir.Generated` namespace,
containing:

- A POCO for every contract type (properties nullable, `[JsonPropertyName]` in camelCase matching
  the wire).
- An `Arg<T>` wrapper for method parameters, `Call` / `BatchEntry` / `Batch` shapes, and
  `Alias` / `Exposes` helpers for dependency chaining.
- One client class per controller and a root `SleipnirGeneratedClient` that owns them.

### 2.3 Use it

```csharp
using Sleipnir.Generated;

var client = new SleipnirGeneratedClient("http://localhost:5001");

// Single typed call — Call<T> deserializes into the generated POCO.
Order order = await client.Call<Order>(client.Order.GetById(42));

// Typed diamond batch (Serial — required for @alias resolution).
var batch = new Batch();
var o = batch.Add(client.Order.GetById(42))
    .Exposes("$.customerId", "@customerId")
    .Exposes("$.id",         "@orderId");
batch.Add(client.Customer.GetById(o.Alias("@customerId")));
var lines = batch.Add(client.OrderLine.GetByOrder(o.Alias("@orderId")))
    .Exposes("$[*].articleId", "@articleIds");
batch.Add(client.Article.GetByIds(lines.Alias("@articleIds")));

SleipnirMultiCallResponse resp = await client.Batch(batch);
var fetchedOrder = resp.Get<Order>("Order.GetById");
var articles     = resp.Get<List<Article>>("Article.GetByIds");
```

Every identifier above — `SleipnirGeneratedClient`, `Order`, `client.Order.GetById`, `Batch`,
`Exposes`, `Alias`, `resp.Get<T>` — is **generated** from `contract.sleipnir.json`. None of it is
hand-written. If the contract changes and you regenerate it on the server, the client rebuild
picks up the new shape; if a method disappears, the client code that calls it stops compiling.

> A complete, compiling example lives in
> [`Sleipnir.Samples.GeneratedClient/Program.cs`](Sleipnir.Samples.GeneratedClient/Program.cs) (the
> Story-01 typed diamond). Its `contract.sleipnir.json` is a copy of the Story-01 server's contract.

### 2.4 Diagnostics

The generator emits two diagnostics, both build-breaking:

| Id | Meaning |
|---|---|
| `SLEIPNIR001` | The contract failed **shape validation** (unknown `discoveryVersion`, an invalid `TypeRef` kind, a `ref` that does not resolve into the `types` registry, …). The generated client is **not** emitted. |
| `SLEIPNIR002` | The contract passed shape validation but the C# emitter threw — a bug in `Sleipnir.Codegen.Core`. The generated client is **not** emitted. |

`SLEIPNIR001` is the one you can cause from a normal workflow (e.g. feeding the generator a contract
from a newer, unsupported `discoveryVersion`). It is additive-only: upgrade the generator package
to one that knows the new version.

### 2.5 Configuration (client)

| Property | Default | Purpose |
|---|---|---|
| `SleipnirContractFile` | `contract.sleipnir.json` if it exists in the project dir | Contract path. The generator matches the AdditionalFile whose filename equals this (case-insensitive). |
| `SleipnirContractEnabled` | `true` | Set `false` to disable the generator for a project. |

---

## 3. The full loop, end to end

A typical "I changed the server" cycle:

```bash
# 1. Change a controller on the server.
#    e.g. rename [SleipnirMethod("GetById")] -> [SleipnirMethod("Get")] on OrderController.

# 2. Build the server — drift is detected, build FAILS.
dotnet build
# error: Sleipnir contract drift detected: the committed contract.sleipnir.json
#        does not match the server's runtime discovery. ...

# 3. The change is intentional — regenerate the contract.
SLEIPNIR_REGEN_GOLDEN=1 dotnet build
git add path/to/contract.sleipnir.json
git commit -m "feat: rename Order.GetById -> Order.Get"

# 4. Update the client's copy of contract.sleipnir.json (or point both at a shared file),
#    then rebuild the client. Calls to client.Order.GetById now fail to compile —
#    fix them to client.Order.Get — exactly the compile-time boundary you want.
dotnet build path/to/client
```

If step 2 did **not** fail, the contract would lie to every client. That is the failure mode this
build action exists to prevent.

---

## 4. In-repo development (ProjectReference) vs published NuGet

The NuGet wiring above assumes packaged packages. When developing inside the Sleipnir repo
(everything is a `ProjectReference`), the same targets/props are imported manually with the
tool/generator paths pointed at their built output. See how Story 01 does it
([`stories/01-n-plus-one-screen/Story01.csproj`](stories/01-n-plus-one-screen/Story01.csproj)):

```xml
<!-- Server: build-order-only ref to the tool (not a runtime dep), then import the targets
     with the tool dll pointed at its built output. -->
<ItemGroup>
  <ProjectReference Include="..\..\Sleipnir.Server.Codegen\Sleipnir.Server.Codegen.csproj"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
<PropertyGroup>
  <SleipnirContractFile>contract.sleipnir.json</SleipnirContractFile>
  <SleipnirExportToolDll>..\..\Sleipnir.Server.Codegen\bin\$(Configuration)\net8.0\Sleipnir.Server.Codegen.dll</SleipnirExportToolDll>
</PropertyGroup>
<Import Project="..\..\Sleipnir.Server.Codegen\build\Sleipnir.Server.Codegen.targets" />
```

For the client generator in-repo, reference the generator project as an analyzer
([`Sleipnir.Samples.GeneratedClient/Sleipnir.Samples.GeneratedClient.csproj`](Sleipnir.Samples.GeneratedClient/Sleipnir.Samples.GeneratedClient.csproj)):

```xml
<ItemGroup>
  <ProjectReference Include="..\..\SleipnirClient\SleipnirClient.csproj" />
  <ProjectReference Include="..\..\Sleipnir.SourceGenerator\Sleipnir.SourceGenerator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
<ItemGroup>
  <AdditionalFiles Include="contract.sleipnir.json" />
</ItemGroup>
```

The published-NuGet and in-repo paths run **the same code** (the same tool dll, the same generator
assembly). The only difference is how the build file is wired in.

---

## 5. CI integration

In CI you want drift to fail the pipeline (that is the gate). Nothing special is needed — a plain
build runs the drift-check target:

```bash
dotnet build Sleipnir.sln -c Release          # server drift-check runs as part of the build
dotnet test  SleipnirTests/SleipnirTests.csproj -c Release --no-build
```

Do **not** set `SLEIPNIR_REGEN_GOLDEN` in CI — that would silently rewrite the contract instead of
failing. Regeneration is a developer action that produces a commit; CI only verifies.

The repo's own CI (`.github/workflows/build.yml`) does exactly this, and additionally packs
`Sleipnir.Generator` and `Sleipnir.Server.Codegen` on tagged releases.

---

## 6. Relationship to the TS `--lang cs` / `sleipnir-gen` path

Sleipnir also ships a TypeScript codegen core (`clients/codegen`, the `sleipnir-gen` npm CLI) that emits
TS, JS, C#, and Python. For the **.NET build** you do not need it — `Sleipnir.Generator` + 
`Sleipnir.Server.Codegen` are Node-free and are the A-prio C# path. The TS path is kept as a
**DevUI/CI convenience**: the Developer UI's C#/Python tabs and any Node-based pipeline use it. It
is not demoted in capability — the C# it produces is byte-for-byte identical to the Roslyn
generator's output, enforced by the parity gate below. It is simply not required on the .NET build
edge.

---

## 7. How this is verified (the gates)

Three automated gates keep the build action honest — all run in `dotnet test`:

| Gate | File | Asserts |
|---|---|---|
| **Parity** | [`SleipnirTests/Unit/Core/CsCodegenParityTests.cs`](SleipnirTests/Unit/Core/CsCodegenParityTests.cs) | The C# emitter (`Sleipnir.Codegen.Core.EmitClient`) is byte-for-byte equal to the committed `clients/codegen/test/snapshots/story01.cs/SleipnirGenerated.cs`, and the emitted file compiles against `Sleipnir.Client` (spawn-`dotnet build` compile gate). |
| **Drift detection** | [`SleipnirTests/Integration/ServerCodegenDriftTests.cs`](SleipnirTests/Integration/ServerCodegenDriftTests.cs) | The export tool against the real Story 01 assembly: happy path (exit 0, committed matches runtime), drift (tampered contract → exit 1, file untouched), regen (`--regen` → exit 0, file repaired). |
| **Generator diagnostics** | [`SleipnirTests/Unit/Core/SourceGeneratorDiagnosticsTests.cs`](SleipnirTests/Unit/Core/SourceGeneratorDiagnosticsTests.cs) | The generator's diagnostic mapping: valid contract → source emitted (no diagnostic), unknown `discoveryVersion` → `SLEIPNIR001`, malformed JSON → `SLEIPNIR002`, source suppressed on both failure paths. |
| **Wire-vs-contract** | [`SleipnirTests/Integration/DiscoveryContractTests.cs`](SleipnirTests/Integration/DiscoveryContractTests.cs) | The live Story 01 server's `GET /api/sleipnir/discovery` equals the committed codegen golden — the contract is the wire, not a sketch. |

The parity gate is the price of having two C# producers (the Roslyn build path and the TS DevUI/CI
path): one input, two producers, equal C#. It is mandatory, not optional.

---

## 8. Troubleshooting

**"Sleipnir contract drift detected" on a build where I didn't touch anything.**
The committed `contract.sleipnir.json` does not match what the built server exposes. This can happen
after a merge, a controller change, or if the contract was never regenerated after the last server
edit. Review the regenerated-vs-committed diff in the build log; if the change is intended, run
`SLEIPNIR_REGEN_GOLDEN=1 dotnet build` and commit the result.

**The generator does not emit anything (no `SleipnirGenerated.cs`).**
The generator only fires on an AdditionalFile whose filename is `contract.sleipnir.json` (or matches
`$(SleipnirContractFile)`). Confirm the file is present in the project directory and included as an
AdditionalFile, and that `SleipnirContractEnabled` is not `false`. The generator runs at compile time
— check the build log for `SLEIPNIR001`/`SLEIPNIR002`.

**`SLEIPNIR001` — "Unsupported discoveryVersion".**
The contract was produced by a newer server than the generator package understands. Upgrade
`Sleipnir.Generator` (and `Sleipnir.Server.Codegen` on the server side) to a matching version. The
schema is additive-only; an older generator cannot read a newer contract by design.

**"Sleipnir export tool error" (exit 2).**
The tool itself failed, not a drift. The exception is printed to the build log. Common cause in
in-repo dev: `SleipnirExportToolDll` points at a tool dll that was not built yet — build the
`Sleipnir.Server.Codegen` project first (or build the whole solution).

**Ambiguous type / `CS0433` "exists in multiple assemblies".**
You have both the generated `SleipnirGenerated.cs` and a hand-written duplicate of a contract type in
the same compilation. The generated types live in `Sleipnir.Generated`; do not re-declare them. If you
share contract types between server and client, let the client use the *generated* ones and keep
the server's as the source.

**The drift-check target does not run.**
It is gated on `$(SleipnirContractFile)` being non-empty (or `contract.sleipnir.json` existing in the
project dir) and `SleipnirContractEnabled != false`. A library project that transitively references
the package but is not a server should set `SleipnirContractEnabled=false` (or simply not configure a
contract file).

---

## 9. Reference — the contract file

`contract.sleipnir.json` is the JSON `SleipnirDiscoveryService.GetDiscoveryInfo()` returns (serialized
with `DiscoverySerialization.Options`: camelCase, nulls omitted). It is the same payload
`GET /api/sleipnir/discovery` serves over HTTP. Top-level shape:

```jsonc
{
  "discoveryVersion": "1",
  "controllers": [ /* { name, methods: [{ methodName, returnType, parameters: [...] }] } */ ],
  "types": { /* "<TypeName>": { kind: "object"|"enum", properties: [...], members: [...] } */ }
}
```

The contract is **the** contract — the server exports it, the client is generated from it, the wire
serves it, and the drift-check guarantees they stay the same file. Schema details:
[`docs/discovery-schema.md`](docs/discovery-schema.md).