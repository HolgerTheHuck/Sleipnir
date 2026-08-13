# sleipnir-linq

A `dotnet tool` that generates the C# service-contract interfaces consumed by
[`Sleipnir.Client.Linq`](../Sleipnir.Client.Linq) from a Sleipnir discovery contract — the typed,
lambda-driven authoring surface for `@alias` dependency chains.

## Install

```bash
dotnet tool install -g Sleipnir.Client.Linq.Codegen
```

## Usage

```bash
sleipnir-linq --discovery contract.sleipnir.json --out SleipnirContracts.g.cs
sleipnir-linq --discovery https://localhost:5001/api/sleipnir/discovery --stdout
sleipnir-linq --discovery contract.sleipnir.json --namespace My.App.Contracts --out Contracts.g.cs
```

| Option | Description |
|---|---|
| `--discovery <url\|file>` | Discovery source. A file path or an `http(s)` URL. Defaults to `contract.sleipnir.json`. |
| `--out <path>` | Write the generated `SleipnirContracts.g.cs` to this path. |
| `--stdout` | Write the generated source to stdout instead of a file. |
| `--namespace <ns>` | C# namespace for the generated file (default `Sleipnir.Linq.Contracts`). |
| `--base-url <url>` | Base URL hint rendered into the file header comment. |

Exit codes: `0` ok, `1` argument/drift error, `2` tool error. The generated file references the
`Sleipnir.Client.Linq` NuGet package (`Arg<T>`, `Dep<T>`, `[SleipnirServiceContract]`,
`[SleipnirMethodContract]`).