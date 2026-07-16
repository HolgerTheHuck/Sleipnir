# Trame.Server.Codegen

A server-side contract **export and drift-check** tool for
[Trame](../README.md) — the code-first, multi-transport RPC framework for .NET 8.
It regenerates the discovery contract (`contract.trame.json`) in-process from a
built server assembly and **fails the build** if the regenerated contract drifts
from the committed one. Part of the [.NET-native codegen trio](../CODEGEN_ONBOARDING.md).

## What it does

- Loads the built server assembly (`Assembly.LoadFrom`, scoped to the server output
  dir — not an AppDomain-wide scan), reflects the `[TrameController]` types, builds a
  `TrameInvoker` with a stub `IServiceScopeFactory` + `NullLogger`, and calls
  `GetDiscoveryInfo()`.
- Serializes the result with `DiscoverySerialization.Options`, sorts controllers by
  name for determinism, and drift-checks against the committed
  `contract.trame.json` (normalize-sort + `JsonNode.DeepEquals`).
- Exit codes: `0` = in sync, `1` = drift detected, `2` = tool error.
  `--regen` (or `TRAME_REGEN_GOLDEN=1`) overwrites the committed contract.

## How it runs

The packed `build/Trame.Server.Codegen.targets` runs the tool `AfterTargets="Build"`
via `<Exec>` (exit 1 → MSBuild error), in its own process so the MSBuild host and
the tool never collide on versions. The tool and its runtime deps ship in
`tasks/net8.0/`.

## Install

```xml
<PackageReference Include="Trame.Server.Codegen" Version="1.0.0" />
```

Targets `net8.0`. Depends on `Trame.Core` + `Trame.Common` (for the discovery +
serialization types). It is a build-time tool, not a runtime dependency.

## Where it fits

This tool *produces* the contract that `Trame.Generator` consumes at compile time.
See [CODEGEN_ONBOARDING.md](../CODEGEN_ONBOARDING.md) for the end-to-end flow and
[CLIENT_GENERATION.md](../CLIENT_GENERATION.md) for the broader codegen story.