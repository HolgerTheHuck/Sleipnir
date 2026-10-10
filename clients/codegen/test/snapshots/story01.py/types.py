# Auto-generated Sleipnir data types. Fields are camelCase (wire).
# Non-nullable fields are always present on the wire (no default); nullable fields
# are Optional and default to None (event frames omit null values — WhenWritingNull).
# DateTime is emitted as str (parse with datetime.fromisoformat if needed).
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Optional

@dataclass
class StockInfo:
    articleId: int
    inStock: int
    @classmethod
    def from_dict(cls, d: dict) -> "StockInfo":
        if d is None:
            return cls(articleId=None, inStock=None)  # type: ignore[arg-type]
        articleId=d.get("articleId")
        inStock=d.get("inStock")
        return cls(articleId=articleId, inStock=inStock)  # type: ignore[call-arg]

@dataclass
class OrderLine:
    articleId: int
    qty: int
    @classmethod
    def from_dict(cls, d: dict) -> "OrderLine":
        if d is None:
            return cls(articleId=None, qty=None)  # type: ignore[arg-type]
        articleId=d.get("articleId")
        qty=d.get("qty")
        return cls(articleId=articleId, qty=qty)  # type: ignore[call-arg]

@dataclass
class Article:
    id: int
    name: str
    price: float
    @classmethod
    def from_dict(cls, d: dict) -> "Article":
        if d is None:
            return cls(id=None, name=None, price=None)  # type: ignore[arg-type]
        id=d.get("id")
        name=d.get("name")
        price=d.get("price")
        return cls(id=id, name=name, price=price)  # type: ignore[call-arg]

@dataclass
class Order:
    id: int
    customerId: int
    shippingAddressId: int
    status: str
    placedAt: str
    note: Optional[str] = None
    @classmethod
    def from_dict(cls, d: dict) -> "Order":
        if d is None:
            return cls(id=None, customerId=None, shippingAddressId=None, status=None, placedAt=None, note=None)  # type: ignore[arg-type]
        id=d.get("id")
        customerId=d.get("customerId")
        shippingAddressId=d.get("shippingAddressId")
        status=d.get("status")
        placedAt=d.get("placedAt")
        note=d.get("note")
        return cls(id=id, customerId=customerId, shippingAddressId=shippingAddressId, status=status, placedAt=placedAt, note=note)  # type: ignore[call-arg]

@dataclass
class Customer:
    id: int
    name: str
    score: Optional[int] = None
    @classmethod
    def from_dict(cls, d: dict) -> "Customer":
        if d is None:
            return cls(id=None, name=None, score=None)  # type: ignore[arg-type]
        id=d.get("id")
        name=d.get("name")
        score=d.get("score")
        return cls(id=id, name=name, score=score)  # type: ignore[call-arg]

@dataclass
class Address:
    id: int
    street: str
    zip: str
    city: str
    @classmethod
    def from_dict(cls, d: dict) -> "Address":
        if d is None:
            return cls(id=None, street=None, zip=None, city=None)  # type: ignore[arg-type]
        id=d.get("id")
        street=d.get("street")
        zip=d.get("zip")
        city=d.get("city")
        return cls(id=id, street=street, zip=zip, city=city)  # type: ignore[call-arg]
