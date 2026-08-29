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
<PackageReference Include="Sleipnir.Server.Codegen" Version="1.0.0" />
```

Targets `net8.0`. Depends on `Sleipnir.Core` + `Sleipnir.Common` (for the discovery +
serialization types). It is a build-time tool, not a runtime dependency.

## Where it fits

This tool *produces* the contract that `Sleipnir.Generator` consumes at compile time.
See [CODEGEN_ONBOARDING.md](../CODEGEN_ONBOARDING.md) for the end-to-end flow and
[CLIENT_GENERATION.md](../CLIENT_GENERATION.md) for the broader codegen story.