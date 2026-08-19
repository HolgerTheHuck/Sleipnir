// JavaScript emitter — transform of the TS output: drop interfaces, emit JSDoc
// `@typedef` blocks (one per ResolvedType) and classes with `@param`/`@returns`
// referencing the typedefs. Same API shape as TS, so `--lang js` loses only the
// hard compile errors and keeps IntelliSense.

import type { EmitterInput, ResolvedController, ResolvedMethod, ResolvedTypeRef } from "../core/model.js";
import { toCamelCase } from "../core/casing.js";
import { tsTypeOfRef, isEventMethod, eventPayloadRef, hasEvents } from "../core/model.js";
import { NamingResolver } from "../core/naming.js";

export interface EmitJsOptions {
  baseUrl?: string;
  /** Same transport contract as the TS emitter (see EmitTsOptions.transport). */
  transport?: "rest" | "ws" | "both";
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

function emitClient(input: EmitterInput, opts: EmitJsOptions): string {
  const transport = opts.transport ?? "rest";
  const events = hasEvents(input);
  const imports = input.controllers.map((c) => `import { ${c.className} } from "./controllers.js";`).join("\n");
  // Event-Controller bekommen `this._subscribe` als zweites ctor-Arg; reine
  // Call-Controller bleiben beim 1-arg-ctor (story01/story02-Snapshots stabil).
  // 2-Space-Einrückung wie das Original (story01/story02-Snapshots byte-identisch).
  const inits = input.controllers.map((c) => {
    const args = c.methods.some(isEventMethod) ? "build, this._subscribe" : "build";
    return `  this.${c.accessor} = new ${c.className}(${args});`;
  });
  const subscribeAssignWs = events
    ? `  this._subscribe = (req, handlers) => this._ws.subscribe(req, handlers);\n`
    : "";
  const subscribeAssignRest = events
    ? `  this._subscribe = async (_req, _handlers) => {\n    throw new Error("Sleipnir events require WebSocket transport. Regenerate with --transport ws|both to subscribe.");\n  };\n`
    : "";

  if (transport === "ws") {
    return `// Auto-generated root Sleipnir client (JS, WebSocket transport).
import { SleipnirCall, SleipnirWebSocketClient } from "sleipnir-client";
${imports}

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {SleipnirWebSocketClientOptions} [options]
   */
  constructor(baseUrl, options = {}) {
    this._ws = new SleipnirWebSocketClient(baseUrl, options);
    const build = (controller, method) => SleipnirCall.init(controller, method);
${subscribeAssignWs}${inits.join("\n")}
  }

  /** @param {TypedCall<*>} call @returns {Promise<SleipnirResponse<*|null>>} */
  async call(call) {
    return this._ws.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batch(b) {
    const m = b.toMulti();
    return this._ws.callBatch(m.requests, m.mode);
  }

  get ws() {
    return this._ws;
  }
}
`;
  }

  if (transport === "both") {
    return `// Auto-generated root Sleipnir client (JS, REST + WebSocket).
import { SleipnirCall, SleipnirRestClient, SleipnirWebSocketClient } from "sleipnir-client";
${imports}

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {{ rest?: SleipnirRestClientOptions, ws?: SleipnirWebSocketClientOptions }} [options]
   */
  constructor(baseUrl, options = {}) {
    this._rest = new SleipnirRestClient(baseUrl, options.rest ?? {});
    this._ws = new SleipnirWebSocketClient(baseUrl, options.ws ?? {});
    const build = (controller, method) => SleipnirCall.init(controller, method);
${subscribeAssignWs}${inits.join("\n")}
  }

  /** @param {TypedCall<*>} call @returns {Promise<SleipnirResponse<*|null>>} */
  async call(call) {
    return this._rest.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batch(b) {
    const m = b.toMulti();
    return this._rest.callBatch(m.requests, m.mode);
  }

  /** @param {TypedCall<*>} call @returns {Promise<SleipnirResponse<*|null>>} */
  async callWs(call) {
    return this._ws.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batchWs(b) {
    const m = b.toMulti();
    return this._ws.callBatch(m.requests, m.mode);
  }

  get rest() {
    return this._rest;
  }

  get ws() {
    return this._ws;
  }
}
`;
  }

  // rest (default).
  return `// Auto-generated root Sleipnir client (JS).
import { SleipnirCall, SleipnirRestClient } from "sleipnir-client";
${imports}

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {SleipnirRestClientOptions} [options]
   */
  constructor(baseUrl, options = {}) {
    this._rest = new SleipnirRestClient(baseUrl, options);
    const build = (controller, method) => SleipnirCall.init(controller, method);
${subscribeAssignRest}${inits.join("\n")}
  }

  /** @param {TypedCall<*>} call @returns {Promise<SleipnirResponse<*|null>>} */
  async call(call) {
    return this._rest.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batch(b) {
    const m = b.toMulti();
    return this._rest.callBatch(m.requests, m.mode);
  }

  get rest() {
    return this._rest;
  }
}
`;
}