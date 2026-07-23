# Contributing to Trame

Thanks for your interest in Trame — pull requests, issues, docs improvements, and new
transport/codegen ideas are all welcome. Trame is **MIT-licensed** and credited to
**Trame Contributors**; by contributing you agree your work is published under that same
license (see [LICENSE](LICENSE)).

> **Trame** is a code-first, command-oriented RPC framework for .NET 8: one C# contract,
> multiple transports (REST / WebSocket / SignalR), with server-side dependency chaining.
> Start with [README.md](README.md) for the tour, [GETTING_STARTED.md](GETTING_STARTED.md)
> for the canonical wiring, and [CLAUDE.md](CLAUDE.md) for the full architecture map.

---

## 1. Ground rules

- **Be kind.** Assume good intent; critique ideas, not people.
- **English for all new code-facing and user-facing text** — comments, log messages,
  console output, domain error strings, `[TrameDocumentation]` text, and NuGet package
  descriptions. Trame targets the international market. (Existing German strings are
  legacy; migrate them opportunistically when you touch the surrounding code — not a
  prerequisite for a PR.)
- **Security issues go private**, not in a public issue. See [SECURITY.md](SECURITY.md)
  and [SECURITY_GUIDE.md](SECURITY_GUIDE.md). For responsible disclosure of a
  vulnerability, please **do not** open a public issue — contact the maintainer privately.

---

## 2. Prerequisites

- **.NET 8 SDK** — `dotnet --version` reports 8.x.
- Optional, for HTTPS/WSS local dev: `dotnet dev-certs https --trust`.
- For UI work in `TrameDeveloperUi/`: Node.js (the dev UI is a Svelte app bundled into the
  Razor package; see `TrameDeveloperUi/` for its own build steps).

---

## 3. Build, test, run

```bash
# Build the whole solution
dotnet build Trame.sln

# Run all tests (xUnit + FluentAssertions + Moq)
dotnet test TrameTests/TrameTests.csproj

# Run a single test class / method
dotnet test TrameTests/TrameTests.csproj --filter "FullyQualifiedName~TrameInvokerTests"
dotnet test TrameTests/TrameTests.csproj --filter "FullyQualifiedName~TrameInvokerTests.MyTestMethod"

# Run the sample app
dotnet run --project Trame/Trame.csproj

# Run benchmarks (Release mode required)
dotnet run -c Release --project TrameBench/TrameBench.csproj
```

CI (`.github/workflows/build.yml`) runs `dotnet build`, `dotnet test`, `dotnet format
--verify-no-changes`, and `dotnet list package --vulnerable` on every push. Run the same
locally before opening a PR so CI is green on the first try:

```bash
dotnet format Trame.sln --verify-no-changes --severity warn
dotnet list Trame.sln package --vulnerable
```

---

## 4. Project layout (the short version)

```
TrameCommon      shared models, attributes, exceptions          → Trame.Common
TrameCore        invoker, discovery, dependency resolver        → Trame.Core
TrameRest        REST transport (minimal APIs)                  → Trame.Rest
TrameWebSocket   WebSocket transport (RFC 6455 middleware)      → Trame.WebSocket
TrameHub         SignalR transport + AddTrame/UseTrame          → Trame.Hub
TrameClient      REST/WS/SignalR clients + fluent TrameCall     → Trame.Client
TrameServer      unified server integration (all transports)    → Trame.Server
TrameTelemetry   optional OpenTelemetry SDK bootstrap           → Trame.Telemetry
TrameDeveloperUi built-in developer web UI                      → Trame.DeveloperUi
Trame.SourceGenerator   Roslyn codegen → typed C# client        → Trame.Generator
Trame.Server.Codegen    server-side contract export/drift-check → Trame.Server.Codegen
Trame            sample app
TrameTests       tests
TrameBench       BenchmarkDotNet
```

The full dependency graph and the core-engine contract (pre-pass auth, batch execution,
alias binding modes, name-uniqueness, tracing) live in [CLAUDE.md](CLAUDE.md). Read it
before touching the engine or transports — it documents the invariants your change must
preserve.

---

## 5. How to propose a change

1. **Open an issue first** for anything beyond a small fix — describe the problem, the
   proposed shape, and alternatives you considered. This avoids wasted work when the
   design needs adjustment.
2. **Fork & branch** from `main`. Use a descriptive branch name
   (`feature/alias-strict-mode`, `fix/batch-auth-prepass`, …).
3. **Keep PRs focused** — one logical change per PR. Easier to review, easier to revert.
4. **Add tests.** New behavior ships with a test in `TrameTests/` (see existing fixtures
   in `TrameTests/Fixtures/` and the per-area organization under `Unit/Core/`,
   `Unit/Client/`, `Unit/Telemetry/`, and `Integration/`).
5. **Update docs** if your change is user-visible — `README.md` / `README_DETAILS.md`,
   `PROTOCOL.md`, `DEPENDENCY_BINDING.md`, `CHANGELOG.md`, and any `[TrameDocumentation]`
   / `[TrameExample]` attributes on affected methods.
6. **Ensure CI is green locally** (build + test + format + vulnerable check, §3).
7. **Open the PR** against `main`. Reference the issue (`Closes #123`).

### Conventions worth knowing

- Controllers are registered by **attribute scanning** (`[TrameController]` /
  `[TrameMethod]`), not manual registration. Dispatch is by `"{Controller}_{Method}"` —
  **no parameter-based overload resolution**; give overloads distinct method names.
- The `TrameInvoker` is a singleton; controllers resolve per-call via
  `IServiceScopeFactory.CreateScope()`. Expression-tree delegates are compiled **once at
  registration**, never per call — don't reintroduce reflection on the hot path.
- Business/domain errors are **returned** via `TrameResults.*`; only unexpected failures
  are **thrown** (→ generic 500). Never throw `TrameException` to set a custom code.
- JSON casing contract: parameter **names** bind case-sensitively; object **value**
  properties read case-insensitively / write camelCase; **JsonPath** is case-sensitive
  against the camelCase wire document (`$.Id` matches nothing — use `$.id`).

---

## 6. Releases

Releases are **tag-driven**: pushing a `v*` tag (e.g. `v1.0.0`) triggers
`.github/workflows/build.yml`, which builds, tests, packs all `Trame.*` packages, pushes
them to nuget.org, and creates a GitHub Release with generated notes. Version is
centralized in `Directory.Build.props`. Maintainers cut tags; contributors don't need to
worry about this.

---

## 7. Licensing

By submitting a pull request, you agree that your contribution is licensed under the
[MIT License](LICENSE), copyright **Trame Contributors**. If you add or bring in
third-party code, attribute it and confirm its license is compatible with MIT.

---

Questions? Open a `question`-tagged issue, or check [ROADMAP.md](ROADMAP.md) for where
the project is headed. Happy hacking.