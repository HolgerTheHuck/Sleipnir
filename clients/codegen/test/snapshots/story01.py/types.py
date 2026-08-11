# Auto-generated Sleipnir data types. Fields are camelCase (wire) and
# default to None (discovery carries no nullability; callers narrow).
# DateTime is emitted as str (parse with datetime.fromisoformat if needed).
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Optional

@dataclass
class StockInfo:
    articleId: Optional[int] = None
    inStock: Optional[int] = None
    @classmethod
    def from_dict(cls, d: dict) -> "StockInfo":
        if d is None:
            return cls(articleId=None, inStock=None)  # type: ignore[arg-type]
        articleId=d.get("articleId")
        inStock=d.get("inStock")
        return cls(articleId=articleId, inStock=inStock)  # type: ignore[call-arg]

@dataclass
class OrderLine:
    articleId: Optional[int] = None
    qty: Optional[int] = None
    @classmethod
    def from_dict(cls, d: dict) -> "OrderLine":
        if d is None:
            return cls(articleId=None, qty=None)  # type: ignore[arg-type]
        articleId=d.get("articleId")
        qty=d.get("qty")
        return cls(articleId=articleId, qty=qty)  # type: ignore[call-arg]

@dataclass
class Article:
    id: Optional[int] = None
    name: Optional[str] = None
    price: Optional[float] = None
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
    id: Optional[int] = None
    customerId: Optional[int] = None
    shippingAddressId: Optional[int] = None
    status: Optional[str] = None
    placedAt: Optional[str] = None
    @classmethod
    def from_dict(cls, d: dict) -> "Order":
        if d is None:
            return cls(id=None, customerId=None, shippingAddressId=None, status=None, placedAt=None)  # type: ignore[arg-type]
        id=d.get("id")
        customerId=d.get("customerId")
        shippingAddressId=d.get("shippingAddressId")
        status=d.get("status")
        placedAt=d.get("placedAt")
        return cls(id=id, customerId=customerId, shippingAddressId=shippingAddressId, status=status, placedAt=placedAt)  # type: ignore[call-arg]

@dataclass
class Customer:
    id: Optional[int] = None
    name: Optional[str] = None
    @classmethod
    def from_dict(cls, d: dict) -> "Customer":
        if d is None:
            return cls(id=None, name=None)  # type: ignore[arg-type]
        id=d.get("id")
        name=d.get("name")
        return cls(id=id, name=name)  # type: ignore[call-arg]

@dataclass
class Address:
    id: Optional[int] = None
    street: Optional[str] = None
    zip: Optional[str] = None
    city: Optional[str] = None
    @classmethod
    def from_dict(cls, d: dict) -> "Address":
        if d is None:
            return cls(id=None, street=None, zip=None, city=None)  # type: ignore[arg-type]
        id=d.get("id")
        street=d.get("street")
        zip=d.get("zip")
        city=d.get("city")
        return cls(id=id, street=street, zip=zip, city=city)  # type: ignore[call-arg]
