# Chapter 7 — The LINQ provider: a typed ergonomic layer over `@alias`

> **Goal:** express the chapter-6 chain (`Search → GetQuotes`) as a typed *query* — `Dep<T>`
> and `SleipnirQuery<T>` infer the `@alias` wiring for you, so the chain reads like LINQ and
> the expose/alias bookkeeping disappears.

## Status: planned

This chapter is **not yet written**. It depends on the `Sleipnir.Client.Linq` package
(`Sleipnir.Client.Linq` — `Dep<T>`, `SleipnirQuery<T>`, `SleipnirLinqClient`, `JsonPathBuilder`)
being complete and **runnable-first** — the same "clone & run" bar every other chapter meets.
Per the package's current state, Tier 1 (`Dep<T>`) and parts of Tier 2 (`SleipnirQuery<T>` B3/B4)
are in place; B1/B2 are pending. The chapter will be written once the package can carry a
runnable admin + portal demo end-to-end.

Everything the LINQ layer builds on — `@alias` placeholders, `dependencyMapping`, `$[*]`
list fan-out into a parameter, Serial resolution, the binding modes — is fully specified in
[Chapter 6 — Chaining](06-chaining.md) and `DEPENDENCY_BINDING.md`. The LINQ provider is a
*frontend* over that wire contract; it adds no new server semantics.

> The running story continues without it: **[Chapter 8 — Auth, JWT Bearer, three
> tiers](08-auth.md)** takes the Market demo protected. None of chapters 8–10 depend on the
> LINQ layer — they use the explicit `.Exposes`/`.Alias` (C#) and `exposes`/`alias` (TS)
> builders from chapter 6.

---

**Next:** [Chapter 8 — Auth, JWT Bearer, three tiers](08-auth.md).