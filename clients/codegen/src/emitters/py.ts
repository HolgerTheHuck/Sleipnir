// Python emitter — emits a self-contained async client (types.py / client.py /
// __init__.py) that depends only on `httpx` (no Trame Python runtime exists).
//
// Headline value: a typed batch builder mirroring the TS runtime. `@dataclass`
// types carry camelCase fields matching the wire (response-only shapes;
// `from_dict` reads camelCase directly — zero alias-map code). `Batch` +
// `exposes`/`alias` mirror TS: `alias(name)` returns the "@x" placeholder
// string, `exposes` strips the leading "@" for the `dependencyMapping` key
// (the server strips "@" from a consumer's "@alias" placeholder before lookup
// — see TrameInvoker.ReplaceDependencyByAliasCore). Method names are
// snake_case (`get_by_id`) while the wire `method` stays the discovery
// `methodName` verbatim ("GetById"); parameter names are verbatim
// (case-sensitive wire binding). The batch `mode` is the integer 1 (Serial)
// — the server's ExecutionMode enum reads an int, not a string.

import type { EmitterInput, ResolvedController, ResolvedMethod, ResolvedProperty, ResolvedTypeRef } from "../core/model.js";
import { pyTypeOfRef } from "../core/model.js";
import { NamingResolver } from "../core/naming.js";

export interface EmitPyOptions {
  /** Base URL hint rendered into the client header comment. */
  baseUrl?: string;
}

/** Emit the Python client as a file tree (types.py / client.py / __init__.py). */
export function emitPyClient(input: EmitterInput, opts: EmitPyOptions = {}): Record<string, string> {
  const resolver = resolverFor(input);
  return {
    "types.py": emitTypes(input, resolver),
    "client.py": emitClient(input, resolver, opts.baseUrl),
    "__init__.py": emitInit(),
  };
}

function resolverFor(input: EmitterInput): NamingResolver {
  const r = new NamingResolver();
  for (const t of input.types) r.register(t.fullName);
  return r;
}

/** snake_case a PascalCase / camelCase identifier (`GetById` → `get_by_id`). */
function snakeCase(name: string): string {
  if (!name) return name;
  // Insert "_" before an uppercase letter that follows a lowercase letter or digit,
  // and before a run of uppercase letters that precedes a lowercase letter.
  let s = name.replace(/([a-z0-9])([A-Z])/g, "$1_$2");
  s = s.replace(/([A-Z]+)([A-Z][a-z])/g, "$1_$2");
  return s.toLowerCase();
}

// ---------------------------------------------------------------------------
// types.py — @dataclass per ResolvedType (camelCase fields matching the wire).
// ---------------------------------------------------------------------------

function emitTypes(input: EmitterInput, resolver: NamingResolver): string {
  const header = `# Auto-generated Trame data types. Fields are camelCase (wire) and
# default to None (discovery carries no nullability; callers narrow).
# DateTime is emitted as str (parse with datetime.fromisoformat if needed).
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Optional

`;
  if (input.types.length === 0) return `${header}# No structured types declared in discovery.\n`;
  const blocks = input.types.map((t) => emitDataclass(t, resolver));
  return header + blocks.join("\n\n") + "\n";
}

function emitDataclass(t: { emittedName: string; properties: ResolvedProperty[] }, resolver: NamingResolver): string {
  const fields = t.properties.map((p) => emitDataclassField(p, resolver));
  const fromDict = emitFromDict(t);
  return `@dataclass\nclass ${t.emittedName}:\n${fields.length ? fields.join("\n") : "    pass"}\n${fromDict}`;
}

function emitDataclassField(p: ResolvedProperty, resolver: NamingResolver): string {
  const ty = pyTypeOfRef(p.typeRef, resolver);
  const todo = p.typeRef.kind === "opaque"
    ? `    # TODO: field "${p.declaredName}" type "${p.typeRef.nativeName ?? "?"}" is an opaque framework/BCL type not modelled in discovery; emitted as Any.\n`
    : "";
  const doc = p.documentation ? `    """${p.documentation}"""\n` : "";
  return `${todo}${doc}    ${p.wireName}: Optional[${ty}] = None`;
}

function emitFromDict(t: { emittedName: string; properties: ResolvedProperty[] }): string {
  const wires = t.properties.map((p) => p.wireName);
  const nulls = wires.map((w) => `${w}=None`).join(", ");
  const assigns = wires.map((w) => `        ${w}=d.get("${w}")`).join("\n");
  return `    @classmethod
    def from_dict(cls, d: dict) -> "${t.emittedName}":
        if d is None:
            return cls(${nulls})  # type: ignore[arg-type]
${assigns}
        return cls(${wires.map((w) => `${w}=${w}`).join(", ")})  # type: ignore[call-arg]`;
}

// ---------------------------------------------------------------------------
// client.py — self-contained async client (httpx + stdlib).
// ---------------------------------------------------------------------------

function emitClient(input: EmitterInput, resolver: NamingResolver, baseUrl?: string): string {
  const header = `# Auto-generated Trame async client. Requires: pip install httpx
# (httpx is imported lazily at call time so \`py_compile\` passes without it).
# Method names are snake_case; the wire method is the discovery methodName verbatim.
# Parameter names are verbatim (case-sensitive wire binding). The batch mode is
# the integer 1 (Serial) — the server ExecutionMode enum reads an int, not a string.`;
  const urlHint = baseUrl ? `# Discovery base URL: ${baseUrl}\n` : "";
  const imports = `from __future__ import annotations

from typing import Any, Optional

from .types import ${input.types.length ? input.types.map((t) => t.emittedName).join(", ") : "Any  # no structured types"}`;

  const runtime = RUNTIME_TEMPLATE;
  const controllerClasses = input.controllers.map((c) => emitControllerClass(c, resolver)).join("\n\n");
  const accessors = input.controllers.map((c) => `        self.${snakeCase(c.name)} = ${c.className}(self)`);

  return `${header}
${urlHint}${imports}


${runtime}
${controllerClasses}

class TrameClient:
    """Root generated Trame client. Async; backed by httpx.

    Use \`call\` / \`call_typed\` for a single call, \`call_batch\` for a
    dependency-chained batch (Serial). Per-controller accessors are below.
    """

    def __init__(self, base_url: str, *, api_path: str = "api/trame", bearer: Optional[str] = None) -> None:
        self._base_url = base_url.rstrip("/")
        self._api_path = api_path.strip("/")
        self._bearer = bearer

${accessors.join("\n")}

    def _url(self, path: str) -> str:
        return f"{self._base_url}/{self._api_path}/{path}"

    async def discover(self) -> dict:
        """GET /api/trame/discovery."""
        import httpx
        headers = self._headers()
        async with httpx.AsyncClient() as client:
            r = await client.get(self._url("discovery"), headers=headers)
            r.raise_for_status()
            return r.json()

    async def call(self, controller: str, method: str, **params: Any) -> dict:
        """POST /api/trame/json with named params; return the raw TrameResponse dict."""
        import httpx
        body = _build_single(controller, method, params)
        headers = self._headers()
        async with httpx.AsyncClient() as client:
            r = await client.post(self._url("json"), json=body, headers=headers)
            return _parse_response(r)

    async def call_typed(self, call: "TrameCall", cls: type) -> Optional[Any]:
        """Execute a single TrameCall and deserialize data into \`cls\` (via from_dict)."""
        resp = await self._post_call(call)
        if not _is_success(resp):
            raise TrameError(resp.get("error") or {"code": resp.get("code", 0), "message": "non-2xx"})
        data = resp.get("data")
        if data is None:
            return None
        if hasattr(cls, "from_dict"):
            return cls.from_dict(data)
        return data

    async def call_batch(self, batch: "Batch") -> list:
        """POST /api/trame/json/multi; return the list of TrameResponse dicts.

        Responses return in topological order (the server runs the topological
        path whenever any request carries a dependencyMapping). Fetch results
        by \`id\` rather than by position.
        """
        import httpx
        body = batch.to_multi()
        headers = self._headers()
        async with httpx.AsyncClient() as client:
            r = await client.post(self._url("json/multi"), json=body, headers=headers)
            text = r.text
            if not r.is_success:
                return [{
                    "code": r.status_code,
                    "id": body["requests"][0]["id"] if body["requests"] else None,
                    "data": None,
                    "error": {"code": r.status_code, "message": f"HTTP Error: {r.status_code}", "details": text},
                }]
            parsed = r.json()
            return parsed if isinstance(parsed, list) else []

    async def _post_call(self, call: "TrameCall") -> dict:
        import httpx
        body = call.to_request()
        headers = self._headers()
        async with httpx.AsyncClient() as client:
            r = await client.post(self._url("json"), json=body, headers=headers)
            return _parse_response(r)

    def _headers(self) -> dict:
        h = {"Content-Type": "application/json"}
        if self._bearer:
            h["Authorization"] = f"Bearer {self._bearer}"
        return h
`;
}

const RUNTIME_TEMPLATE = `# --- Wire helpers (mirror the TS/C# request builders) ---

def _build_params(params: dict) -> list:
    """Named params → [{parameterName, num, data}] (data is a native JSON value)."""
    out = []
    for i, (key, value) in enumerate(params.items()):
        out.append({"parameterName": key, "num": i, "data": value})
    return out


def _build_single(controller: str, method: str, params: dict, *,
                  id: Optional[str] = None, dependency_mapping: Optional[dict] = None) -> dict:
    return {
        "controller": controller,
        "method": method,
        "params": _build_params(params),
        "id": id if id is not None else f"{controller}.{method}",
        "dependencyMapping": dependency_mapping,
        "binaryData": None,
    }


def _parse_response(r: Any) -> dict:
    """Parse a TrameResponse; on non-2xx HTTP, synthesize an error response."""
    text = r.text
    if not r.is_success:
        return {
            "code": r.status_code,
            "id": None,
            "data": None,
            "error": {"code": r.status_code, "message": f"HTTP Error: {r.status_code}", "details": text},
        }
    parsed = r.json()
    if "isSuccess" not in parsed:
        code = parsed.get("code", 0) or 0
        parsed["isSuccess"] = 200 <= code <= 299
    return parsed


def _is_success(resp: dict) -> bool:
    if "isSuccess" in resp:
        return bool(resp["isSuccess"])
    code = resp.get("code", 0) or 0
    return 200 <= code <= 299


class TrameError(Exception):
    """Raised when a Trame call returns a non-2xx logical response."""

    def __init__(self, error: dict) -> None:
        self.code = error.get("code", 0)
        self.message = error.get("message", "Trame error")
        self.details = error.get("details")
        super().__init__(f"[{self.code}] {self.message}")


class TrameCall:
    """A single Trame call built by a generated controller method."""

    def __init__(self, controller: str, method: str, params: dict) -> None:
        self._controller = controller
        self._method = method
        self._params = params
        self._id: Optional[str] = None
        self._dependency_mapping: dict = {}

    def named(self, id: str) -> "TrameCall":
        self._id = id
        return self

    def exposes(self, path: str, alias: str) -> "TrameCall":
        # The wire dependencyMapping key is the alias WITHOUT the leading '@'
        # (the server strips '@' from a consumer's '@alias' placeholder before lookup).
        key = alias[1:] if alias.startswith("@") else alias
        self._dependency_mapping[key] = path
        return self

    def to_request(self) -> dict:
        return _build_single(
            self._controller, self._method, self._params,
            id=self._id, dependency_mapping=self._dependency_mapping or None,
        )


class _BatchEntry:
    """A call enrolled in a batch. exposes/alias mirror the TS runtime."""

    def __init__(self, call: TrameCall) -> None:
        self._call = call

    def exposes(self, path: str, alias: str) -> "_BatchEntry":
        self._call.exposes(path, alias)
        return self

    def alias(self, name: str) -> str:
        """Return the '@alias' wire placeholder (for a consumer parameter)."""
        return name


class Batch:
    """Batch builder for dependency-chained calls (Serial mode).

    Add calls in topological order: a producer's \`exposes\` must run before any
    consumer's \`alias\`.
    """

    def __init__(self) -> None:
        self._calls: list[TrameCall] = []

    def add(self, call: TrameCall) -> _BatchEntry:
        self._calls.append(call)
        return _BatchEntry(call)

    def to_multi(self) -> dict:
        return {"requests": [c.to_request() for c in self._calls], "mode": 1}
`;


// ---------------------------------------------------------------------------
// Per-controller classes — snake_case methods returning TrameCall.
// ---------------------------------------------------------------------------

function emitControllerClass(ctrl: ResolvedController, resolver: NamingResolver): string {
  const methods = ctrl.methods.map((m) => emitMethod(ctrl, m, resolver));
  return `class ${ctrl.className}:\n    def __init__(self, owner: "TrameClient") -> None:\n        self._owner = owner\n\n${methods.join("\n\n")}`;
}

function emitMethod(ctrl: ResolvedController, m: ResolvedMethod, resolver: NamingResolver): string {
  const params = m.parameters.map((p) => {
    const ty = pyTypeOfRef(p.typeRef, resolver);
    return `${p.name}: ${ty}`;
  });
  const paramDict = m.parameters.map((p) => `"${p.name}": ${p.name}`).join(", ");
  const doc = m.documentation ? `    """${m.documentation}"""\n` : "";
  const todo = m.returnType.kind === "opaque" && !m.isVoid
    ? `    # TODO: return type "${m.returnType.nativeName ?? "?"}" is an opaque framework/BCL type not modelled in discovery.\n`
    : "";
  const snake = snakeCase(m.methodName);
  return `${todo}${doc}    def ${snake}(self${params.length ? ", " + params.join(", ") : ""}) -> TrameCall:
        return TrameCall("${ctrl.name}", "${m.methodName}", {${paramDict}})`;
}

// ---------------------------------------------------------------------------
// __init__.py — re-exports types + client.
// ---------------------------------------------------------------------------

function emitInit(): string {
  return `# Auto-generated barrel.
from .types import *  # noqa: F401,F403
from .client import TrameClient, TrameCall, Batch, TrameError  # noqa: F401
`;
}