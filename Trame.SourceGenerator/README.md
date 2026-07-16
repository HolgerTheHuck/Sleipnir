# Trame.Generator

A Roslyn incremental source generator that turns a committed Trame discovery
contract into a **typed C# client at compile time** — no Node, no T4, no runtime
reflection. Part of the [.NET-native codegen trio](../CODEGEN_ONBOARDING.md) that
follows from treating the discovery JSON as the standard contract.

## What it does

- Reads `contract.trame.json` (as an `AdditionalFile`; honors `$(TrameContractFile)`).
- Emits `TrameGenerated.cs` — controllers, methods, and the contract types from the
  discovery schema, so call-site typos in controller / method / parameter names fail
  to compile instead of failing at runtime.
- Reports `TRAME001` (unknown / unsupported `discoveryVersion`) and `TRAME002`
  (malformed contract JSON) as build diagnostics; on either, no source is emitted.

## Install

```xml
<PackageReference Include="Trame.Generator" Version="1.0.0" />
<AdditionalFiles Include="contract.trame.json" />
```

The packed `build/Trame.Generator.props` auto-marks `contract.trame.json` as an
AdditionalFile, so the `<AdditionalFiles>` line is only needed for a non-default
file name or location. The generator assembly ships in `analyzers/dotnet/cs/` and is
self-contained (the `Trame.Codegen.Core` sources are linked in, so there is no
runtime dependency to resolve). Pair it with `Trame.Client` for the transport.

## Where it fits

The contract is produced by `Trame.Server.Codegen` (export + drift-check), and this
generator consumes it. See [CODEGEN_ONBOARDING.md](../CODEGEN_ONBOARDING.md) for the
end-to-end flow and [CLIENT_GENERATION.md](../CLIENT_GENERATION.md) for the broader
codegen story.