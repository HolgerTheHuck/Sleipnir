# Sleipnir.Server.Codegen

A server-side contract **export and drift-check** tool for
[Sleipnir](../README.md) — the code-first, multi-transport RPC framework for .NET 8.
It regenerates the discovery contract (`contract.sleipnir.json`) in-process from a
built server assembly and **fails the build** if the regenerated contract drifts
from the committed one. Part of the [.NET-native codegen trio](../CODEGEN_ONBOARDING.md).

## What it does

- Loads the built server assembly (`Assembly.LoadFrom`, scoped to the server output
  dir — not an AppDomain-wide scan), reflects the `[SleipnirController]` types, builds a
  `SleipnirInvoker` with a stub `IServiceScopeFactory` + `NullLogger`, and calls
  `GetDiscoveryInfo()`.
- Serializes the result with `DiscoverySerialization.Options`, sorts every order-incidental
  collection by name for determinism (controllers, methods, contract-type properties, enum
  members — parameters keep signature order for positional `num` binding), and drift-checks
  against the committed `contract.sleipnir.json` (normalize-sort of those same arrays +
  `JsonNode.DeepEquals`). Consequence: the committed file only ever churns on real contract
  changes — a moved C# member or a reflection-order shift never produces a git diff.
- Exit codes: `0` = in sync, `1` = drift detected, `2` = tool error.
  `--regen` (or `SLEIPNIR_REGEN_GOLDEN=1`) overwrites the committed contract.

## How it runs

The packed `build/Sleipnir.Server.Codegen.targets` runs the tool `AfterTargets="Build"`
via `<Exec>` (exit 1 → MSBuild error), in its own process so the MSBuild host and
the tool never collide on versions. The tool and its runtime deps ship in
`tasks/net8.0/`.

## Install

```xml
<PackageReference Include="Sleipnir.Server.Codegen" Version="1.8.1" />
```

Targets `net8.0`. Depends on `Sleipnir.Core` + `Sleipnir.Common` (for the discovery +
serialization types). It is a build-time tool, not a runtime dependency.

## First run — create the committed contract

The gate needs a committed `contract.sleipnir.json` to compare against. Create it once and
commit it:

```bash
SLEIPNIR_REGEN_GOLDEN=1 dotnet build          # bash
$env:SLEIPNIR_REGEN_GOLDEN='1'; dotnet build  # PowerShell
```

The build log says `created committed contract at 'contract.sleipnir.json'`. Every later
build then drift-checks against it. Until the file exists, each build warns
`SLEIPNIRCG001` (the gate is inactive). A project that references the package but is not a
Sleipnir server sets `<SleipnirContractEnabled>false</SleipnirContractEnabled>`; another file
location goes into `<SleipnirContractFile>`.

## Compatibility gate — protect deployed clients

The drift gate keeps the file current. Name a **baseline** — the contract the clients in the
field were built against — and the same build also refuses breaking changes against it:

```xml
<SleipnirContractBaseline>git:v1.7.0</SleipnirContractBaseline>  <!-- or a file path -->
```

Every difference is classified as breaking / soft / safe (warnings `SLEIPNIRCG003` /
`SLEIPNIRCG002`); a breaking one fails the build unless `SleipnirBreakingChanges=warn`.
`SleipnirContractRollbackCheck=true` also checks the new clients against the old server,
`SleipnirContractCompatReport=<path>` writes a JSON report. Rules and recipes:
[CONTRACT_EVOLUTION.md](../CONTRACT_EVOLUTION.md).

## Where it fits

This tool *produces* the contract that `Sleipnir.Generator` (C#) consumes at compile time,
and the file `sleipnir-gen` (npm `sleipnir-codegen`, TS/JS/Python) generates from:
`sleipnir-gen --lang ts --discovery path/to/contract.sleipnir.json --out src/api`. Generate
committed clients from this file rather than from a live discovery URL — the file never lags
or leads the server build it was checked against.
See [CODEGEN_ONBOARDING.md](../CODEGEN_ONBOARDING.md) for the end-to-end flow and
[CLIENT_GENERATION.md](../CLIENT_GENERATION.md) for the broader codegen story.