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
<PackageReference Include="Sleipnir.Server.Codegen" Version="1.7.0" />
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

## Where it fits

This tool *produces* the contract that `Sleipnir.Generator` (C#) consumes at compile time,
and the file `sleipnir-gen` (npm `sleipnir-codegen`, TS/JS/Python) generates from:
`sleipnir-gen --lang ts --discovery path/to/contract.sleipnir.json --out src/api`. Generate
committed clients from this file rather than from a live discovery URL — the file never lags
or leads the server build it was checked against.
See [CODEGEN_ONBOARDING.md](../CODEGEN_ONBOARDING.md) for the end-to-end flow and
[CLIENT_GENERATION.md](../CLIENT_GENERATION.md) for the broader codegen story.