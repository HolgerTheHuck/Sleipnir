// Kanonische Wire-Typen des Sleipnir-Protokolls (camelCase, siehe PROTOCOL.md).
// Port aus SleipnirDeveloperUi/src/lib/types/discovery.ts; Binary-Felder sind hier
// korrekt als base64-String getypt (System.Text.Json serialisiert byte[] als
// base64), nicht als number[].

/** Ausführungsmodus für Batch-Requests (SleipnirMultiRequest.mode). */
export enum ExecutionMode {
  /** 0 — alle Requests parallel (Dependencies werden ignoriert). */
  Parallel = 0,
  /** 1 — seriell, mit @alias-Abhängigkeitsauflösung (topologisch). */
  Serial = 1,
}

/** Lebenszyklus-Zustand des WebSocket-Clients (Spiegel von C# SleipnirConnectionState). */
export enum SleipnirConnectionState {
  /** 0 — keine aktive Verbindung (vor dem ersten Connect oder nach erschöpftem Reconnect). */
  Disconnected = 0,
  /** 1 — Verbindungsaufbau läuft. */
  Connecting = 1,
  /** 2 — Verbindung steht; Calls können gesendet werden. */
  Connected = 2,
  /** 3 — unerwarteter Disconnect; Hintergrund-Reconnect mit Backoff läuft. */
  Reconnecting = 3,
}

/** Strukturierter Fehler im SleipnirResponse.error-Feld (code != 2xx). */
export interface SleipnirErrorBody {
  code: number;
  message: string;
  details?: string | null;
  requestId?: string | null;
}

/** Ein einzelner Parameter innerhalb von SleipnirRequest.params. */
export interface SleipnirParameter {
  /** Parametername (Server bindet danach). Bei Positionalen leer/ein Platzhalter. */
  parameterName: string;
  /** Nativer JSON-Wert (Zahl, String, Bool, Objekt, Array), kein JSON-String mehr.
   *  Ein @alias-Platzhalter ist ein String-Wert mit @-Präfix (z. B. "@newId"). */
  data: unknown;
  /** Positionaler Index (Fallback, wenn parameterName nicht bindet). */
  num?: number;
}

/** Einzelner RPC-Request. */
export interface SleipnirRequest {
  controller: string;
  method: string;
  /** Parameter als natives Array von SleipnirParameter (data ist nativer JSON-Wert). */
  params?: SleipnirParameter[] | null;
  id?: string;
  /** alias → JsonPath; Werte aus dieser Response werden für Folgerequests exposed. */
  dependencyMapping?: Record<string, string> | null;
  /** base64-kodiertes Binary (für byte[]-Parameter der Zielmethode). */
  binaryData?: string | null;
}

/** Batch-Request (mehrere Calls in einem Roundtrip). */
export interface SleipnirMultiRequest {
  requests: SleipnirRequest[];
  mode: ExecutionMode;
}

/** Antwort eines RPC-Calls. */
export interface SleipnirResponse {
  /** Logischer Status-Code (im Body, nicht HTTP-Status). 200–299 = Erfolg. */
  code: number;
  /** Strukturierter Ergebniswert (roh, null bei 204/void/Fehler). Seit dem
   *  Single-Pass-Fix kein JSON-String mehr, sondern der geparste Wert. */
  data?: unknown | null;
  /** base64-kodiertes Binary-Result (für byte[]-Rückgaben). */
  content?: string | null;
  /** Korrelations-Id (spiegelt request.id). */
  id?: string | null;
  /** Aufgelöste alias → Wert-Map für Dependency-Chaining. */
  exposedDependencies?: Record<string, string> | null;
  /** Strukturierter Fehler bei non-2xx. */
  error?: SleipnirErrorBody | null;
  /**
   * true, wenn code 200–299. Server-seitig `[JsonIgnore]` und aus `code`
   * abgeleitet — das Wire-Frame enthält dieses Feld NICHT. Der Client füllt
   * es beim Parsen auf (siehe `normalizeResponse`); es ist daher optional.
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
  kind: "scalar" | "array" | "set" | "map" | "ref" | "stream" | "opaque" | "void";
  /** scalar: a name from the fixed scalar table. */
  name?: string;
  /** array | set | stream: the element TypeRef. */
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