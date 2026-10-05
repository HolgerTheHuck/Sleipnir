// Canonical wire types of the Sleipnir protocol (camelCase, see PROTOCOL.md).
// Ported from SleipnirDeveloperUi/src/lib/types/discovery.ts; binary fields are typed
// as base64 strings here (System.Text.Json serializes byte[] as base64), not as
// number[].

/** Execution mode for batch requests (SleipnirMultiRequest.mode). */
export enum ExecutionMode {
  /** 0 — all requests in parallel (dependencies are ignored). */
  Parallel = 0,
  /** 1 — serial, with @alias dependency resolution (topological). */
  Serial = 1,
}

/**
 * Bearer token source: a fixed string or a provider function that is resolved fresh
 * per call (REST) or per connect/reconnect (WS) — for rotating JWTs without rebuilding
 * the client. Swappable at runtime via `setBearer`.
 */
export type BearerProvider = string | (() => string);

/** Lifecycle state of the WebSocket client (mirror of C# SleipnirConnectionState). */
export enum SleipnirConnectionState {
  /** 0 — no active connection (before the first connect, or after reconnect exhaustion). */
  Disconnected = 0,
  /** 1 — connection attempt in progress. */
  Connecting = 1,
  /** 2 — connection is up; calls can be sent. */
  Connected = 2,
  /** 3 — unexpected disconnect; background auto-reconnect with backoff is running. */
  Reconnecting = 3,
}

/**
 * Aggregated, transport-neutral connection status of a `SleipnirTransportRouter`
 * (`router.connection`). See `SleipnirTransportRouter.connection` for the transitions.
 *
 * - `"connecting"` — the first connection (or the `auto` probe) is being established.
 * - `"open"` — the active transport is usable (the `rest` profile: always — REST is stateless).
 * - `"reconnecting"` — an established connection dropped; the client is reconnecting.
 * - `"closed"` — not connected (before first use on WS/SignalR, after reconnect gave up, or
 *   after `dispose()`).
 */
export type SleipnirConnectionStatus = "connecting" | "open" | "reconnecting" | "closed";

/**
 * Semantic error category — mirror of the server enum `SleipnirCommon.Results.SleipnirErrorCategory`
 * (see `ERROR_CATALOG.md` §2). Layered *on top of* the numeric `code`; transport-uniform. On the
 * wire the server writes the enum **name** (PascalCase, `JsonStringEnumConverter`); `"None"` means
 * "no category set" (fall back to `code`). New categories may be added in minor versions — treat
 * an unknown value like `"None"`.
 */
export type SleipnirErrorCategory =
  | "None"
  | "InvalidArgument"
  | "Unauthenticated"
  | "PermissionDenied"
  | "NotFound"
  | "Conflict"
  | "FailedPrecondition"
  | "ResourceExhausted"
  | "Internal"
  | "Unavailable"
  | "Cancelled";

/** All {@link SleipnirErrorCategory} values, in server enum order (index = numeric value). */
export const SLEIPNIR_ERROR_CATEGORIES: readonly SleipnirErrorCategory[] = [
  "None",
  "InvalidArgument",
  "Unauthenticated",
  "PermissionDenied",
  "NotFound",
  "Conflict",
  "FailedPrecondition",
  "ResourceExhausted",
  "Internal",
  "Unavailable",
  "Cancelled",
];

/** Structured error in the SleipnirResponse.error field (code != 2xx). */
export interface SleipnirErrorBody {
  code: number;
  message: string;
  details?: string | null;
  requestId?: string | null;
  /**
   * Semantic category (additive; absent on transport-synthesized errors, e.g. a non-2xx HTTP
   * status, and on servers that predate it). See {@link SleipnirErrorCategory}.
   */
  category?: SleipnirErrorCategory;
}

/** A single parameter within SleipnirRequest.params. */
export interface SleipnirParameter {
  /** Parameter name (the server binds by it). Empty/a placeholder for positional. */
  parameterName: string;
  /** Native JSON value (number, string, bool, object, array), no JSON string anymore.
   *  An @alias placeholder is a string value with an @ prefix (e.g. "@newId"). */
  data: unknown;
  /** Positional index (fallback when parameterName does not bind). */
  num?: number;
}

/** A single RPC request. */
export interface SleipnirRequest {
  controller: string;
  method: string;
  /** Parameters as a native array of SleipnirParameter (data is a native JSON value). */
  params?: SleipnirParameter[] | null;
  id?: string;
  /** alias → JsonPath; values from this response are exposed for follow-up requests. */
  dependencyMapping?: Record<string, string> | null;
  /** base64-encoded binary (for byte[] parameters of the target method). */
  binaryData?: string | null;
}

/** Batch request (multiple calls in one roundtrip). */
export interface SleipnirMultiRequest {
  requests: SleipnirRequest[];
  mode: ExecutionMode;
}

/** The response of an RPC call. */
export interface SleipnirResponse {
  /** Logical status code (in the body, not the HTTP status). 200–299 = success. */
  code: number;
  /** Structured result value (raw, null on 204/void/error). Since the
   *  single-pass fix no JSON string but the parsed value. */
  data?: unknown | null;
  /** base64-encoded binary result (for byte[] returns). */
  content?: string | null;
  /** Correlation id (mirrors request.id). */
  id?: string | null;
  /** Resolved alias → value map for dependency chaining. */
  exposedDependencies?: Record<string, string> | null;
  /** Structured error on non-2xx. */
  error?: SleipnirErrorBody | null;
  /**
   * true when code is 200–299. Server-side `[JsonIgnore]` and derived from `code`
   * — the wire frame does NOT contain this field. The client fills it in while
   * parsing (see `normalizeResponse`); it is therefore optional.
   */
  isSuccess?: boolean;
}

// --- Discovery (GET /api/sleipnir/discovery) ---

export interface DiscoveryInfo {
  /** Schema version (additive-only). See docs/discovery-schema.md §11. */
  discoveryVersion: string;
  controllers: ControllerMeta[];
  types: Record<string, TypeMeta>;
}

export interface ControllerMeta {
  name: string;
  methods: MethodMeta[];
}

export interface MethodMeta {
  methodName: string;
  returnType: TypeRef;
  parameters: ParameterMeta[];
  documentation?: string | null;
}

export interface ParameterMeta {
  parameterName: string;
  parameterType: TypeRef;
  /** C# default parameter value (compile-time constant), or null/absent when none. */
  defaultValue?: unknown;
  documentation?: string | null;
}

export interface TypeMeta {
  /** "object" | "enum". */
  kind: string;
  /** Opaque registry key (identity, not type syntax). Doubles as the `types` key. */
  typeName: string;
  properties: PropertyMeta[];
  /** Enum members, present when kind === "enum". */
  members?: EnumMember[];
  example?: unknown;
}

export interface PropertyMeta {
  propertyName: string;
  propertyType: TypeRef;
}

export interface EnumMember {
  name: string;
  value?: unknown;
}

/**
 * Language-neutral type reference (docs/discovery-schema.md §2). Discriminated by `kind`.
 */
export interface TypeRef {
  kind: "scalar" | "array" | "set" | "map" | "ref" | "stream" | "event" | "opaque" | "void";
  /** scalar: a name from the fixed scalar table. */
  name?: string;
  /** array | set | stream | event: the element TypeRef (for events, the pushed payload type T of IObservable<T>). */
  element?: TypeRef;
  /** map: the key TypeRef. */
  key?: TypeRef;
  /** map: the value TypeRef. */
  value?: TypeRef;
  /** ref: the opaque key into DiscoveryInfo.types. */
  ref?: string;
  /** opaque: diagnostic hint of the unmodelled framework/BCL type (never identity). */
  nativeName?: string;
  /** Occurrence-level nullability from C# NRT. Absent ⟹ not-nullable. */
  nullable?: boolean;
}