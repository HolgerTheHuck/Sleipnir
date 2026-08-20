# Chapter 10 — Production: interceptors, observability, binary

> **Goal:** take the 3-tier app from "runs" to "production-shaped" — an interceptor pipeline
> that logs every call, `/metrics` (Prometheus) and `/observability` (JSON) endpoints, the
> `Sleipnir` `ActivitySource` for distributed tracing, and `byte[]`/`SleipnirResponse.Content`
> for binary payloads.

## Status: planned

This chapter is not yet written. It cross-links the existing production surfaces rather than
inventing new ones:

- **Interceptors** — `ISleipnirInterceptor` / `SleipnirInvocationDelegate` (the middleware
  pipeline), with the built-in `SleipnirLoggingInterceptor` as the example.
- **Observability** — the opt-in `/metrics` (Prometheus exposition) and `/observability`
  (JSON) endpoints and the DevUI observability panel.
- **Tracing** — the always-on `ActivitySource("Sleipnir")` (cost-neutral with no listener);
  consumers opt in via `AddSleipnirTelemetry` or their own OTel pipeline.
- **Binary** — `byte[]` parameters (raw, from `SleipnirRequest.BinaryData`) and
  `SleipnirResponse.Content`; the blessed pattern for media/raw resources (a co-hosted ASP.NET
  `GET` endpoint, not an RPC method).

> The running story so far: [Chapter 9 — Eventing](09-events.md) (planned) is the climax.

---

**Next:** _this is the final planned chapter._