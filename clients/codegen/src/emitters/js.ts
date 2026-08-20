// JavaScript emitter — transform of the TS output: drop interfaces, emit JSDoc
// `@typedef` blocks (one per ResolvedType) and classes with `@param`/`@returns`
// referencing the typedefs. Same API shape as TS, so `--lang js` loses only the
// hard compile errors and keeps IntelliSense.

import type { EmitterInput, ResolvedController, ResolvedMethod, ResolvedTypeRef } from "../core/model.js";
import { toCamelCase } from "../core/casing.js";
import { tsTypeOfRef, isEventMethod, eventPayloadRef, hasEvents } from "../core/model.js";
import { NamingResolver } from "../core/naming.js";

/** Which backends the generated `SleipnirClient` bundles (mirrors the TS emitter). */
export type SleipnirBundleCapability = "rest" | "ws" | "all" | "signalr";

export interface EmitJsOptions {
  baseUrl?: string;
  /**
   * Codegen capability — which backends the generated `SleipnirClient` bundles. Default `all`.
   * See `EmitTsOptions.capability` for the full contract; the public JS surface is identical
   * across all capabilities (transport selected at runtime via `SleipnirTransportRouter`).
   */
  capability?: SleipnirBundleCapability;
  /** DEPRECATED alias for `capability` (sse→rest, both→all). Use `capability` instead. */
  transport?: "rest" | "sse" | "ws" | "both";
}

/** Emit the full JS client as a file tree. */
export function emitJsClient(input: EmitterInput, opts: EmitJsOptions = {}): Record<string, string> {
  const resolver = resolverFor(input);
  return {
    "api/types.js": emitTypeDefs(input, resolver),
    "api/controllers.js": emitControllers(input, resolver),
    "api/client.js": emitClient(input, opts),
    "api/index.js": `// Auto-generated barrel.\nexport { SleipnirClient } from "./client.js";\nexport * from "./controllers.js";\n`,
  };
}

function resolverFor(input: EmitterInput): NamingResolver {
  const r = new NamingResolver();
  for (const t of input.types) r.register(t.fullName);
  return r;
}

// ---------------------------------------------------------------------------
// types.js — JSDoc @typedef blocks.
// ---------------------------------------------------------------------------

function emitTypeDefs(input: EmitterInput, resolver: NamingResolver): string {
  const blocks: string[] = [];
  for (const t of input.types) {
    const props = t.properties.map((p) => {
      const ty = jsDocTypeOf(p.typeRef, resolver);
      return ` * @property {${ty}} ${p.wireName}`;
    });
    blocks.push(`/**\n * @typedef {Object} ${t.emittedName}${props.length ? "\n" + props.join("\n") : ""}\n */`);
  }
  if (blocks.length === 0) return "// No structured types declared in discovery.\n";
  return `// Auto-generated Sleipnir data types (JSDoc). Properties are camelCase (wire).\n\n${blocks.join("\n\n")}\n`;
}

/** JSDoc type string for a resolved ref (arrays render as `T[]`). */
function jsDocTypeOf(ref: ResolvedTypeRef, resolver: NamingResolver): string {
  return tsTypeOfRef(ref, resolver);
}

// ---------------------------------------------------------------------------
// controllers.js — classes with @param/@returns.
// ---------------------------------------------------------------------------

function emitControllers(input: EmitterInput, resolver: NamingResolver): string {
  const classes = input.controllers.map((c) => emitControllerClass(c, resolver));
  return `// Auto-generated Sleipnir controllers (JSDoc-typed JS).
import { SleipnirCall } from "sleipnir-client";
${classes.join("\n\n")}
`;
}

function emitControllerClass(ctrl: ResolvedController, resolver: NamingResolver): string {
  const events = ctrl.methods.some(isEventMethod);
  const methods = ctrl.methods.map((m) =>
    isEventMethod(m) ? emitEventMethod(ctrl, m, resolver) : emitMethod(ctrl, m, resolver),
  );
  if (!events) {
    return `export class ${ctrl.className} {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
${methods.join("\n\n")}
}`;
  }
  return `export class ${ctrl.className} {
  /**
   * @param {(controller: string, method: string) => SleipnirCall} build
   * @param {(req: SleipnirRequest, handlers: SubscribeHandlers<unknown>) => Promise<SleipnirSubscription>} subscribe
   */
  constructor(build, subscribe) {
    this._build = build;
    this._subscribe = subscribe;
  }
${methods.join("\n\n")}
}`;
}

/**
 * Emit a typed `subscribe` method (JSDoc) for a `[SleipnirEvent]` (IObservable<T>)
 * method. Builds the wire request via `SleipnirCall` and delegates to the root
 * client's `_subscribe`, which sends `kind:"subscribe"` over WebSocket. Events
 * are NOT chainable (no exposes/@alias).
 */
function emitEventMethod(ctrl: ResolvedController, m: ResolvedMethod, resolver: NamingResolver): string {
  const payloadType = tsTypeOfRef(eventPayloadRef(m), resolver);
  const paramDocs = m.parameters.map((p) => {
    const tsName = toCamelCase(p.name);
    const ty = jsDocTypeOf(p.typeRef, resolver);
    return `   * @param {${ty}} ${tsName}`;
  });
  const params = m.parameters.map((p) => toCamelCase(p.name));
  const withEntries = m.parameters.map((p) => `${p.name}: ${toCamelCase(p.name)}`);
  const withCall = withEntries.length ? `.with({ ${withEntries.join(", ")} })` : "";
  const doc = m.documentation ? `   * ${m.documentation}\n` : "";
  return `  /**\n${doc}${paramDocs.join("\n")}\n   * @param {SubscribeHandlers<${payloadType}>} handlers\n   * @returns {Promise<SleipnirSubscription>}\n   */
  async ${m.emittedName}(${[...params, "handlers"].join(", ")}) {
    return this._subscribe(this._build("${ctrl.name}", "${m.methodName}")${withCall}.toRequest(), handlers);
  }`;
}

function emitMethod(ctrl: ResolvedController, m: ResolvedMethod, resolver: NamingResolver): string {
  const retType = m.isVoid ? "void" : tsTypeOfRef(m.returnType, resolver);
  const paramDocs = m.parameters.map((p) => {
    const tsName = toCamelCase(p.name);
    const ty = jsDocTypeOf(p.typeRef, resolver);
    return `   * @param {${ty}} ${tsName}`;
  });
  const params = m.parameters.map((p) => toCamelCase(p.name));
  const withEntries = m.parameters.map((p) => `${p.name}: ${toCamelCase(p.name)}`);
  const withCall = withEntries.length ? `.with({ ${withEntries.join(", ")} })` : "";
  const returns = m.isVoid ? "" : `   * @returns {Promise<SleipnirResponse<${retType} | null>>}\n`;
  const doc = m.documentation ? `   * ${m.documentation}\n` : "";
  return `  /**\n${doc}${paramDocs.join("\n")}\n${returns}   */
  async ${m.emittedName}(${params.join(", ")}) {
    const call = this._build("${ctrl.name}", "${m.methodName}")${withCall};
    return call;
  }`;
}

// ---------------------------------------------------------------------------
// client.js — root client.
// ---------------------------------------------------------------------------

/** Canonicalize the capability option (mirrors the TS emitter). */
function resolveCapability(opts: EmitJsOptions): SleipnirBundleCapability {
  if (opts.capability) return opts.capability;
  switch (opts.transport) {
    case "rest": return "rest";
    case "sse": return "rest";
    case "ws": return "ws";
    case "both": return "all";
    default: return "all";
  }
}

function emitClient(input: EmitterInput, opts: EmitJsOptions): string {
  const capability = resolveCapability(opts);
  const events = hasEvents(input);
  const imports = input.controllers.map((c) => `import { ${c.className} } from "./controllers.js";`).join("\n");
  // Event-Controller bekommen `this._subscribe` als zweites ctor-Arg; reine
  // Call-Controller bleiben beim 1-arg-ctor (story01/story02-Snapshots stabil).
  // 2-Space-Einrückung wie das Original (story01/story02-Snapshots byte-identisch).
  const inits = input.controllers.map((c) => {
    const args = c.methods.some(isEventMethod) ? "build, this._subscribe" : "build";
    return `  this.${c.accessor} = new ${c.className}(${args});`;
  });
  // `_subscribe` delegiert an den Transport-Router, der den WS-vs-SSE-Unterschied
  // kapselt (WS reicht das Request durch; SSE entpackt es zu (controller, method, params)).
  const subscribeAssign = events
    ? `  this._subscribe = (req, handlers) => this._router.subscribe(req, handlers);\n`
    : "";

  return `// Auto-generated root Sleipnir client (JS, capability: ${capability}).
// Transport is selected at runtime via SleipnirTransportRouter: "auto" (default) probes
// WebSocket and falls back to REST+SSE on failure; useTransport() switches explicitly.
import { SleipnirCall, SleipnirTransportRouter } from "sleipnir-client";
${imports}

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {object} [options] per-backend options (rest/ws/sse/signalr) + shared bearer,
   *   callTimeout, probeTimeout, defaultTransport. Passed to SleipnirTransportRouter.
   */
  constructor(baseUrl, options = {}) {
    this._router = new SleipnirTransportRouter({ baseUrl, capability: "${capability}", ...options });
    const build = (controller, method) => SleipnirCall.init(controller, method);
${subscribeAssign}${inits.join("\n")}
  }

  /** @returns {Promise<void>} resolve the \`auto\` profile (probe WS → fallback REST+SSE). */
  negotiate() {
    return this._router.negotiate();
  }

  /** @param {string} t @returns {Promise<void>} switch the active transport at runtime. */
  useTransport(t) {
    return this._router.useTransport(t);
  }

  /** @returns {string|null} the resolved transport profile (null until \`auto\` is negotiated). */
  get activeTransport() {
    return this._router.activeTransport;
  }

  /** @param {TypedCall<*>} call @returns {Promise<SleipnirResponse<*|null>>} */
  async call(call) {
    return this._router.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batch(b) {
    const m = b.toMulti();
    return this._router.callBatch(m.requests, m.mode);
  }

  /** @returns {SleipnirRestClient|undefined} underlying REST client (escape hatch). */
  get rest() {
    return this._router.rest;
  }

  /** @returns {SleipnirWebSocketClient|undefined} underlying WebSocket client (escape hatch). */
  get ws() {
    return this._router.ws;
  }

  /** @returns {SleipnirSseClient|undefined} underlying SSE client (escape hatch). */
  get sse() {
    return this._router.sse;
  }

  /** @returns {SleipnirSignalrClient|undefined} underlying SignalR client (escape hatch). */
  get signalr() {
    return this._router.signalr;
  }

  /** @param {string|Function} bearer swap the bearer on all bundled backends. */
  setBearer(bearer) {
    this._router.setBearer(bearer);
  }

  /** Dispose all bundled backends (terminal). */
  dispose() {
    this._router.dispose();
  }
}
`;
}