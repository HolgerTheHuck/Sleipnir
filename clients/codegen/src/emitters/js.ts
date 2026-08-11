// JavaScript emitter — transform of the TS output: drop interfaces, emit JSDoc
// `@typedef` blocks (one per ResolvedType) and classes with `@param`/`@returns`
// referencing the typedefs. Same API shape as TS, so `--lang js` loses only the
// hard compile errors and keeps IntelliSense.

import type { EmitterInput, ResolvedController, ResolvedMethod, ResolvedTypeRef } from "../core/model.js";
import { toCamelCase } from "../core/casing.js";
import { tsTypeOfRef } from "../core/model.js";
import { NamingResolver } from "../core/naming.js";

export interface EmitJsOptions {
  baseUrl?: string;
}

/** Emit the full JS client as a file tree. */
export function emitJsClient(input: EmitterInput, _opts: EmitJsOptions = {}): Record<string, string> {
  const resolver = resolverFor(input);
  return {
    "api/types.js": emitTypeDefs(input, resolver),
    "api/controllers.js": emitControllers(input, resolver),
    "api/client.js": emitClient(input),
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
  const methods = ctrl.methods.map((m) => emitMethod(ctrl, m, resolver));
  return `export class ${ctrl.className} {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
${methods.join("\n\n")}
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

function emitClient(input: EmitterInput): string {
  const imports = input.controllers.map((c) => `import { ${c.className} } from "./controllers.js";`).join("\n");
  const accessors = input.controllers.map((c) => `  this.${c.accessor} = new ${c.className}(build);`);
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
${accessors.join("\n")}
  }

  /** @param {TypedCall<*>} call @returns {Promise<SleipnirResponse<*|null>>} */
  async call(call) {
    return this._rest.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batch(b) {
    return this._rest.callBatch(b.toMulti());
  }

  get rest() {
    return this._rest;
  }
}
`;
}