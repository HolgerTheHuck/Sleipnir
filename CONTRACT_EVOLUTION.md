# Contract Evolution

> How to change a Sleipnir API without breaking the clients that are already deployed — and how
> the build tells you when a change would. Companion to [`STABILITY.md`](STABILITY.md) (which
> covers the *framework's* stability) and [`CODEGEN_ONBOARDING.md`](CODEGEN_ONBOARDING.md) (the
> committed contract and its drift gate).

---

## TL;DR

- **New parameters are optional** (nullable or with a C# default). **Outputs only grow** (new
  properties, new methods). Everything that renames or removes goes through a new route
  (`[SleipnirController("Customer.v2")]`) next to the old one.
- The **compatibility gate** checks this mechanically: it compares the contract your server
  builds now with the contract deployed clients were built against (a *baseline* — typically the
  `contract.sleipnir.json` of your last release) and classifies every difference as
  **breaking**, **soft** or **safe**.
  - Server build: `<SleipnirContractBaseline>git:v1.7.0</SleipnirContractBaseline>` — a breaking
    change fails the build.
  - Any CI: `sleipnir-gen diff --base base.json --head contract.sleipnir.json` — exit 5 on a
    breaking change.
- Both run the same rules (a C# and a TS engine, kept identical by a shared executable spec:
  [`clients/codegen/test/fixtures/compat/cases.json`](clients/codegen/test/fixtures/compat/cases.json)).

## 1. What the wire tolerates (the facts the rules rest on)

| Fact | Consequence |
|---|---|
| Dispatch is by name: `{controller}_{method}`, ordinal | Renaming a controller or method is a new route; the old one is gone (404 / 400). |
| Parameters bind by `parameterName`, case-sensitive | Renaming a parameter means the old name binds to nothing. Generated clients and named calls send `num: -1` (name-only), so this fails loudly: `400 Missing required parameter`. |
| An omitted parameter binds its C# default, `null` when nullable, otherwise `400` (since 1.7.0) | Adding a **required** parameter breaks old clients; adding an **optional** one does not. |
| Unknown JSON properties are ignored, object properties match case-insensitively | Extra fields are harmless in both directions; renaming `Name` → `name` in C# is invisible. |
| Missing request-object properties bind a silent default (`AliasBindingMode` `Weak`) | Adding a non-nullable property to an *input* DTO is a soft change: old clients send nothing, the server sees `0`/`null`. |
| Enums travel as numbers | Renaming a member is invisible; renumbering one changes its meaning; a new member reaches old clients as an unknown number. |
| Types are anonymous on the wire | Moving or renaming a C# class (`Api.Dto.Order` → `Api.Contracts.OrderDto`) is invisible — the check compares types **structurally**. |
| `List<T>`, `T[]`, `HashSet<T>`, `IAsyncEnumerable<T>` are all JSON arrays | Switching between them is invisible. |

## 2. Positions and directions

The same change can be harmless or fatal depending on where the type is used:

- **Input** — parameters and the request objects inside them. The *client* produces, the
  *server* accepts. Widening is safe (`int` → `long`, required → optional, non-null → nullable).
- **Output** — return values, event payloads, response objects. The *server* produces, the
  *client* accepts. Narrowing is safe; widening breaks (`int` → `long` overflows a C# client,
  non-null → nullable surprises every client that never checks).

A type used in both positions is checked in both — the stricter result wins naturally.

**Directions.** The default, `old-clients`, answers *"can the clients built against the baseline
still talk to the new server?"* — the normal rollout order (server first, clients follow). With
the rollback check (`SleipnirContractRollbackCheck` / `--rollback`) the gate also answers *"can
the new clients still talk to the old server?"* — what you need when a server deployment may be
rolled back after clients have updated. The rollback pass reports only breaking and soft changes.

## 3. The rules

Severity: **breaking** = a deployed client fails (400, 404, deserialization error, a `null` it
never checks); **soft** = it keeps working but a value is silently lost, defaulted or not
understood; **safe** = invisible or purely additive.

### Routes

| Change (baseline → current) | id | Severity |
|---|---|---|
| Controller removed / renamed | `controller-removed` | breaking |
| Method removed / renamed | `method-removed` | breaking |
| Method switched between call and `[SleipnirEvent]` | `method-kind-changed` | breaking |
| Controller / method added | `controller-added` / `method-added` | safe |

### Parameters (input)

| Change | id | Severity |
|---|---|---|
| Required parameter added (non-nullable, no default) | `required-parameter-not-sent` | breaking |
| Parameter renamed (same position, compatible type) | `parameter-renamed` | breaking |
| Optional parameter became required | `parameter-may-be-omitted` | breaking |
| Parameter type narrowed or changed kind (`double` → `int`, `int` → `string`) | `type-incompatible` | breaking |
| Parameter removed — old clients' value is ignored | `parameter-not-accepted` | soft |
| Parameter order changed — positional callers (`.With(a, b)`) bind wrongly | `parameter-order-changed` | soft |
| Default value changed — callers that omit it get the new default | `parameter-default-changed` | soft |
| Optional parameter added (nullable, C# default, or `byte[]`) | `optional-parameter-added` | safe |
| Type widened (`int` → `long`/`double`/`decimal`, `float` → `double`, `char` → `string`) | — | safe |

### Objects

| Change | Position | id | Severity |
|---|---|---|---|
| Required property removed | output | `property-not-sent` | breaking |
| Optional (nullable) property removed | output | `property-not-sent` | soft |
| Property added | output | `property-not-accepted` | safe |
| Non-nullable property added | input | `property-not-sent` | soft (silent default; `400` under `AliasBindingMode` `Strict`/`Paranoid` for alias-sourced / all parameters) |
| Nullable property added | input | `property-not-sent` | safe |
| Property removed | input | `property-not-accepted` | soft |
| non-null → nullable | output | `null-not-accepted` | breaking |
| nullable → non-null | input | `null-not-accepted` | breaking |
| Property type changed incompatibly | both | `type-incompatible` | breaking |

### Enums (compared by numeric wire value)

| Change | Position | id | Severity |
|---|---|---|---|
| Member's number changed | both | `enum-value-changed` | breaking |
| Member removed | input | `enum-value-not-accepted` | breaking (the server binds an undefined value) |
| Member added | output | `enum-value-not-accepted` | soft (old clients receive an unknown number) |
| Member removed | output / added in input | — | safe |

### Everything else

| Change | id | Severity |
|---|---|---|
| Return value changed type (incl. `T` → `void`) | `type-incompatible` | breaking |
| Return value may now be `null` | `null-not-accepted` | breaking |
| Map key/value or array element changed | `type-incompatible` (site `…{value}`, `…[]`) | breaking |
| A modelled type became opaque (or `any`) | `opaque-changed` | soft — cannot be verified |
| An opaque type became modelled | — | safe |

Sites name exactly where: `Order.Find(take)`, `Order.Get -> return.Customer.Name`,
`Feed.Ticks -> event.Price`, `Order.SetState(state)::Draft`, `Stats.Get -> return{value}`.

## 4. Recipes

**Rename a parameter.** Don't. Add the new name as an optional parameter, keep reading the old
one, mark the old one in the docs; drop it in the next major. Or add a new method.

**Rename or reshape a method.** Add the new method (or a `Customer.v2` controller), keep the old
one delegating to it. Remove the old one only when no client uses it anymore.

**Add a parameter.** Make it nullable or give it a C# default (`int take = 20`). The TS/JS client
omits `undefined` values (since 1.8.0), so the C# default applies for new clients too; an explicit
`null` stays an explicit `null`.

**Add a field to a response.** Always fine. Removing one is not — keep it, filled or `null` with
the property declared nullable *from the start*.

**Add an enum member that the server returns.** Soft: old clients receive a number they don't
know. Give clients a fallback (`default:` branch) before you add members; then the warning is
expected.

**Make an output nullable.** Breaking for every client that never checks. Return an empty object
or a typed envelope (`SleipnirResponse<T>` with `SleipnirResults.NotFound`) instead.

**A real break.** `[SleipnirController("Customer.v2")]` next to `Customer`. Both routes live
until the old clients are gone (STABILITY.md §4, routing-key versioning).

## 5. Setting it up

### Server build (MSBuild, `Sleipnir.Server.Codegen`)

The compatibility gate runs in the same target as the drift gate. It is **off** until you name
a baseline.

| Property | Default | Meaning |
|---|---|---|
| `SleipnirContractBaseline` | empty (off) | The contract deployed clients were built against: a file path, or `git:<ref>` — this project's contract file as of that ref (`git show <ref>:./contract.sleipnir.json`). |
| `SleipnirBreakingChanges` | `error` | `error`: a breaking change fails the build. `warn`: report only. |
| `SleipnirContractRollbackCheck` | `false` | Also check current clients against the baseline server. |
| `SleipnirContractCompatReport` | empty | Write the JSON report here (CI artifact, PR comment). |

```xml
<PropertyGroup>
  <!-- Clients in the field were built against the last release. -->
  <SleipnirContractBaseline>git:v1.7.0</SleipnirContractBaseline>
</PropertyGroup>
```

Findings appear as build warnings — `SLEIPNIRCG003` breaking, `SLEIPNIRCG002` soft — and a
breaking change ends the build with one summary error. An unavailable baseline (unknown ref, a
file that does not exist yet) is warning `SLEIPNIRCG004`, never a silent skip.

> **CI:** `git:<ref>` needs the ref in the clone. `actions/checkout` fetches neither history nor
> tags by default — use `fetch-depth: 0` (or `git fetch --tags`) before the build.

### Any pipeline (`sleipnir-gen diff`)

```bash
git show v1.7.0:Api/contract.sleipnir.json > base.json
npx sleipnir-gen diff --base base.json --head Api/contract.sleipnir.json            # exit 5 on breaking
npx sleipnir-gen diff --base base.json --head Api/contract.sleipnir.json --fail-on soft --rollback --json
```

`--base`/`--head` take a file, an `http(s)://…/api/sleipnir/discovery` URL, or `-` (stdin). The
JSON report has the same shape as `SleipnirContractCompatReport`:
`{ "summary": { "breaking", "soft", "safe" }, "changes": [ { "id", "severity", "site", "position", "direction", "message" } ] }`.

### Accepting a deliberate break

1. Prefer a compatible shape (§4).
2. If the break is intended: set `SleipnirBreakingChanges=warn` for that change, ship it, and move
   the baseline to the release that contains it — from then on the gate protects the new shape.

## 6. What the check cannot see

- **Semantics.** A method that keeps its signature but changes what it does is invisible.
- **Renames are a heuristic.** A removed and an added parameter at the same position with a
  compatible type are reported as one rename; otherwise as two findings.
- **Parameters without nullable annotations** (`#nullable disable`) count as required — the server
  binds `null` for them, so the check is stricter than the runtime there.
- **Custom JSON settings.** The rules assume Sleipnir's wire defaults (numeric enums, camelCase,
  no `AllowReadingFromString`). A host that serializes enums as strings makes renaming a member
  breaking and renumbering harmless.
- **Opaque types** (not modelled in discovery) are compared by name only (`opaque-changed`).
