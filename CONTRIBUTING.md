# Contributing to Sleipnir

Thanks for your interest in Sleipnir — pull requests, issues, docs improvements, and new
transport/codegen ideas are all welcome. Sleipnir is **MIT-licensed** and credited to
**Sleipnir Contributors**; by contributing you agree your work is published under that same
license (see [LICENSE](LICENSE)).

> **Sleipnir** is a code-first, command-oriented RPC framework for .NET 8: one C# contract,
> multiple transports (REST / WebSocket / SignalR), with server-side dependency chaining.
> Start with [README.md](README.md) for the tour, [GETTING_STARTED.md](GETTING_STARTED.md)
> for the canonical wiring, and [CLAUDE.md](CLAUDE.md) for the full architecture map.

---

## 1. Ground rules

- **Be kind.** Assume good intent; critique ideas, not people.
- **English for all new code-facing and user-facing text** — comments, log messages,
  console output, domain error strings, `[SleipnirDocumentation]` text, and NuGet package
  descriptions. Sleipnir targets the international market. (Existing German strings are
  legacy; migrate them opportunistically when you touch the surrounding code — not a
  prerequisite for a PR.)
- **Security issues go private**, not in a public issue. See [SECURITY.md](SECURITY.md)
  and [SECURITY_GUIDE.md](SECURITY_GUIDE.md). For responsible disclosure of a
  vulnerability, please **do not** open a public issue — contact the maintainer privately.

---

## 2. Prerequisites

- **.NET 8 SDK** — `dotnet --version` reports 8.x.
- Optional, for HTTPS/WSS local dev: `dotnet dev-certs https --trust`.
- For UI work in `SleipnirDeveloperUi/`: Node.js (the dev UI is a Svelte app bundled into the
  Razor package; see `SleipnirDeveloperUi/` for its own build steps).

---

## 3. Build, test, run

```bash
# Build the whole solution
dotnet build Sleipnir.sln

# Run all tests (xUnit + FluentAssertions + Moq)
dotnet test SleipnirTests/SleipnirTests.csproj

# Run a single test class / method
dotnet test SleipnirTests/SleipnirTests.csproj --filter "FullyQualifiedName~SleipnirInvokerTests"
dotnet test SleipnirTests/SleipnirTests.csproj --filter "FullyQualifiedName~SleipnirInvokerTests.MyTestMethod"

# Run the sample app
dotnet run --project Sleipnir/Sleipnir.csproj

# Run benchmarks (Release mode required)
dotnet run -c Release --project SleipnirBench/SleipnirBench.csproj
```

CI (`.github/workflows/build.yml`) runs `dotnet build`, `dotnet test`, `dotnet format
--verify-no-changes`, and `dotnet list package --vulnerable` on every push. Run the same
locally before opening a PR so CI is green on the first try:

```bash
dotnet format Sleipnir.sln --verify-no-changes --severity warn
dotnet list Sleipnir.sln package --vulnerable
```

---

## 4. Project layout (the short version)

```
SleipnirCommon      shared models, attributes, exceptions          → Sleipnir.Common
SleipnirCore        invoker, discovery, dependency resolver        → Sleipnir.Core
SleipnirRest        REST transport (minimal APIs)                  → Sleipnir.Rest
SleipnirWebSocket   WebSocket transport (RFC 6455 middleware)      → Sleipnir.WebSocket
SleipnirHub         SignalR transport + AddSleipnir/UseSleipnir          → Sleipnir.Hub
SleipnirClient      REST/WS/SignalR clients + fluent SleipnirCall     → Sleipnir.Client
SleipnirServer      unified server integration (all transports)    → Sleipnir.Server
SleipnirTelemetry   optional OpenTelemetry SDK bootstrap           → Sleipnir.Telemetry
SleipnirDeveloperUi built-in developer web UI                      → Sleipnir.DeveloperUi
Sleipnir.SourceGenerator   Roslyn codegen → typed C# client        → Sleipnir.Generator
Sleipnir.Server.Codegen    server-side contract export/drift-check → Sleipnir.Server.Codegen
Sleipnir            sample app
SleipnirTests       tests
SleipnirBench       BenchmarkDotNet
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
4. **Add tests — every bug fix ships with a regression test.** New behavior ships with a
   test in `SleipnirTests/` (see existing fixtures in `SleipnirTests/Fixtures/` and the per-area
   organization under `Unit/Core/`, `Unit/Client/`, `Unit/Telemetry/`, and `Integration/`).
   A bug fix — especially a hotfix — is not complete without a regression test that fails
   before the fix and passes after. A security/correctness hotfix merged without a test is
   a defect of its own: the fix can silently regress on the next refactor and the original
   fault stays undocumented. If you cannot write a deterministic test, say why in the PR.
5. **Update docs** if your change is user-visible — `README.md` / `README_DETAILS.md`,
   `PROTOCOL.md`, `DEPENDENCY_BINDING.md`, `CHANGELOG.md`, and any `[SleipnirDocumentation]`
   / `[SleipnirExample]` attributes on affected methods.
6. **Ensure CI is green locally** (build + test + format + vulnerable check, §3).
7. **Open the PR** against `main`. Reference the issue (`Closes #123`).

### Conventions worth knowing

- Controllers are registered by **attribute scanning** (`[SleipnirController]` /
  `[SleipnirMethod]`), not manual registration. Dispatch is by `"{Controller}_{Method}"` —
  **no parameter-based overload resolution**; give overloads distinct method names.
- The `SleipnirInvoker` is a singleton; controllers resolve per-call via
  `IServiceScopeFactory.CreateScope()`. Expression-tree delegates are compiled **once at
  registration**, never per call — don't reintroduce reflection on the hot path.
- Business/domain errors are **returned** via `SleipnirResults.*`; only unexpected failures
  are **thrown** (→ generic 500). Never throw `SleipnirException` to set a custom code.
- JSON casing contract: parameter **names** bind case-sensitively; object **value**
  properties read case-insensitively / write camelCase; **JsonPath** is case-sensitive
  against the camelCase wire document (`$.Id` matches nothing — use `$.id`).

---

## 6. Releases

Releases are **tag-driven**: pushing a `v*` tag (e.g. `v1.0.0`) triggers
`.github/workflows/build.yml`, which builds, tests, packs all `Sleipnir.*` packages, pushes
them to nuget.org, and creates a GitHub Release with generated notes. Version is
centralized in `Directory.Build.props`. Maintainers cut tags; contributors don't need to
worry about this.

### Version pins: `scripts/bump-versions.ps1`

The NuGet packages get their version from the tag, but the repo also carries a set of
**version pins** that would otherwise drift: template/samples `PackageReference`s (which
decide what `dotnet new sleipnir-server` scaffolds), npm pins in the template SPA, and the
PackageReference / npm snippets in the docs. Before cutting a tag, run:

```bash
pwsh scripts/bump-versions.ps1 -Version 1.4.3 -DryRun   # review what changes
pwsh scripts/bump-versions.ps1 -Version 1.4.3           # apply
```

Release checklist:

1. **Bump pins** with the script (above); review the diff — every changed line must be a
   version-shaped pin, never prose.
2. **npm pins are decoupled** from the NuGet lockstep (npm publishes dispatch-only). If the
   published `sleipnir-client` version lags the tag (`-NpmVersion 1.4.1`), pass it
   explicitly — a default run assumes both sides ride the same version.
3. **Stale sample/template `package-lock.json`**: the committed locks reference localfeed
   `.tgz` artifacts (not tracked) and are regenerated by `npm install`, never edited by
   hand — leave them out of the bump commit if they differ only cosmetically.
4. **Tag it** (`git tag v1.4.3 && git push origin v1.4.3`) — the NuGet lockstep stamps the
   packages. npm ships separately via the dispatch workflow when the client/codegen
   versions are ready.

---

## 7. Licensing

By submitting a pull request, you agree that your contribution is licensed under the
[MIT License](LICENSE), copyright **Sleipnir Contributors**. If you add or bring in
third-party code, attribute it and confirm its license is compatible with MIT.

---

Questions? Open a `question`-tagged issue, or check [ROADMAP.md](ROADMAP.md) for where
the project is headed. Happy hacking.