// sleipnir-codegen — typed client stub generator for Sleipnir.
//
// One pure-TS core emits typed clients in TypeScript / JavaScript (C# + Python
// ship in Increment 2). Consumes the runtime discovery payload
// (`GET /api/sleipnir/discovery`) as the contract spec. See CLIENT_GENERATION.md
// for the roadmap and design rationale.
//
// This barrel is **browser-safe** (no Node-only imports) so the DevUI and any
// browser consumer can import `sleipnir-codegen` directly. Node-only entry points
// — discovery loading (fs, stdin) — live in `sleipnir-codegen/node`.

// Casing + scalars — the wire-correctness primitives.
export { toCamelCase, shortName, pascalCase } from "./core/casing.js";
export {
  NUMBER_NAMES, BOOL_NAMES, STRING_NAMES, ANY_NAMES, VALUE_TYPE_NAMES,
  isNumberName, isBoolName, isStringName, isAnyName, isValueTypeRef, isScalar,
  isVoidReturn, tsTypeOf, csTypeOf, pyTypeOf, defaultValueForType,
} from "./core/scalars.js";

// Shape model (pure primitives; the DevUI checker stays in the DevUI).
export {
  type JsonKind, type TypeShape,
  lookupTypeMeta, findProperty, shapeFromRef, returnShape, paramShape, propertyShape,
} from "./core/shapes.js";

// Naming resolver.
export { NamingResolver } from "./core/naming.js";

// Emitter input model.
export {
  type ResolvedTypeRef, type ResolvedProperty, type ResolvedType,
  type ResolvedParameter, type ResolvedMethod, type ResolvedController,
  type EmitterInput,
  buildEmitterInput, resolveTypeRef, tsTypeOfRef, csTypeOfRef, pyTypeOfRef,
} from "./core/model.js";

// Emitters.
export { emitTsClient, type EmitTsOptions } from "./emitters/ts.js";
export { emitJsClient, type EmitJsOptions } from "./emitters/js.js";
export { emitCsClient, type EmitCsOptions } from "./emitters/cs.js";
export { emitPyClient, type EmitPyOptions } from "./emitters/py.js";