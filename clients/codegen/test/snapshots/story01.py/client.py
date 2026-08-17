# Auto-generated Sleipnir async client. Requires: pip install httpx
# (httpx is imported lazily at call time so `py_compile` passes without it).
# Method names are snake_case; the wire method is the discovery methodName verbatim.
# Parameter names are verbatim (case-sensitive wire binding). The batch mode is
# the integer 1 (Serial) — the server ExecutionMode enum reads an int, not a string.
from __future__ import annotations

from typing import Any, Optional

from .types import StockInfo, OrderLine, Article, Order, Customer, Address


# --- Wire helpers (mirror the TS/C# request builders) ---

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
    """Parse a SleipnirResponse; on non-2xx HTTP, synthesize an error response."""
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


class SleipnirError(Exception):
    """Raised when a Sleipnir call returns a non-2xx logical response."""

    def __init__(self, error: dict) -> None:
        self.code = error.get("code", 0)
        self.message = error.get("message", "Sleipnir error")
        self.details = error.get("details")
        super().__init__(f"[{self.code}] {self.message}")


class SleipnirCall:
    """A single Sleipnir call built by a generated controller method."""

    def __init__(self, controller: str, method: str, params: dict) -> None:
        self._controller = controller
        self._method = method
        self._params = params
        self._id: Optional[str] = None
        self._dependency_mapping: dict = {}

    def named(self, id: str) -> "SleipnirCall":
        self._id = id
        return self

    def exposes(self, path: str, alias: str) -> "SleipnirCall":
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

    def __init__(self, call: SleipnirCall) -> None:
        self._call = call

    def exposes(self, path: str, alias: str) -> "_BatchEntry":
        self._call.exposes(path, alias)
        return self

    def alias(self, name: str) -> str:
        """Return the '@alias' wire placeholder (for a consumer parameter).

        '@'-normalization is symmetric with exposes(): exposes() strips a leading
        '@' (the wire dependencyMapping key is the bare name — the server strips the
        consumer's '@alias' placeholder before lookup), while alias() ENSURES a
        leading '@' (the consumer sends data: "@alias"). So both call styles work:
        alias('ids') -> '@ids' and alias('@ids') -> '@ids'. Returning the bare name
        (the 1.2.1 bug) sent 'ids' on the wire, which the server never matched.
        """
        return name if name.startswith("@") else "@" + name


class Batch:
    """Batch builder for dependency-chained calls (Serial mode).

    Add calls in topological order: a producer's `exposes` must run before any
    consumer's `alias`.
    """

    def __init__(self) -> None:
        self._calls: list[SleipnirCall] = []

    def add(self, call: SleipnirCall) -> _BatchEntry:
        self._calls.append(call)
        return _BatchEntry(call)

    def to_multi(self) -> dict:
        return {"requests": [c.to_request() for c in self._calls], "mode": 1}

class StockClient:
    def __init__(self, owner: "SleipnirClient") -> None:
        self._owner = owner

    def get_by_articles(self, articleIds: list[int]) -> SleipnirCall:
        return SleipnirCall("Stock", "GetByArticles", {"articleIds": articleIds})

class OrderLineClient:
    def __init__(self, owner: "SleipnirClient") -> None:
        self._owner = owner

    def get_by_order(self, orderId: int) -> SleipnirCall:
        return SleipnirCall("OrderLine", "GetByOrder", {"orderId": orderId})

class ArticleClient:
    def __init__(self, owner: "SleipnirClient") -> None:
        self._owner = owner

    def get_by_ids(self, articleIds: list[int]) -> SleipnirCall:
        return SleipnirCall("Article", "GetByIds", {"articleIds": articleIds})

class OrderClient:
    def __init__(self, owner: "SleipnirClient") -> None:
        self._owner = owner

    def get_by_id(self, id: int) -> SleipnirCall:
        return SleipnirCall("Order", "GetById", {"id": id})

class CustomerClient:
    def __init__(self, owner: "SleipnirClient") -> None:
        self._owner = owner

    def get_by_id(self, customerId: int) -> SleipnirCall:
        return SleipnirCall("Customer", "GetById", {"customerId": customerId})

class AddressClient:
    def __init__(self, owner: "SleipnirClient") -> None:
        self._owner = owner

    def get_by_id(self, addressId: int) -> SleipnirCall:
        return SleipnirCall("Address", "GetById", {"addressId": addressId})

class SleipnirClient:
    """Root generated Sleipnir client. Async; backed by httpx.

    Use `call` / `call_typed` for a single call, `call_batch` for a
    dependency-chained batch (Serial). Per-controller accessors are below.
    """

    def __init__(self, base_url: str, *, api_path: str = "api/sleipnir", bearer: Optional[str] = None) -> None:
        self._base_url = base_url.rstrip("/")
        self._api_path = api_path.strip("/")
        self._bearer = bearer

        self.stock = StockClient(self)
        self.order_line = OrderLineClient(self)
        self.article = ArticleClient(self)
        self.order = OrderClient(self)
        self.customer = CustomerClient(self)
        self.address = AddressClient(self)

    def _url(self, path: str) -> str:
        return f"{self._base_url}/{self._api_path}/{path}"

    async def discover(self) -> dict:
        """GET /api/sleipnir/discovery."""
        import httpx
        headers = self._headers()
        async with httpx.AsyncClient() as client:
            r = await client.get(self._url("discovery"), headers=headers)
            r.raise_for_status()
            return r.json()

    async def call(self, controller: str, method: str, **params: Any) -> dict:
        """POST /api/sleipnir/json with named params; return the raw SleipnirResponse dict."""
        import httpx
        body = _build_single(controller, method, params)
        headers = self._headers()
        async with httpx.AsyncClient() as client:
            r = await client.post(self._url("json"), json=body, headers=headers)
            return _parse_response(r)

    async def call_typed(self, call: "SleipnirCall", cls: type) -> Optional[Any]:
        """Execute a single SleipnirCall and deserialize data into `cls` (via from_dict)."""
        resp = await self._post_call(call)
        if not _is_success(resp):
            raise SleipnirError(resp.get("error") or {"code": resp.get("code", 0), "message": "non-2xx"})
        data = resp.get("data")
        if data is None:
            return None
        if hasattr(cls, "from_dict"):
            return cls.from_dict(data)
        return data

    async def call_batch(self, batch: "Batch") -> list:
        """POST /api/sleipnir/json/multi; return the list of SleipnirResponse dicts.

        Responses return in topological order (the server runs the topological
        path whenever any request carries a dependencyMapping). Fetch results
        by `id` rather than by position.
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

    async def _post_call(self, call: "SleipnirCall") -> dict:
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
