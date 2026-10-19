"""Client for the Etsy Shop Manager service's Home Assistant API."""

from __future__ import annotations

import asyncio
from typing import Any

import aiohttp

from .const import API_PREFIX


class EtsyShopManagerError(Exception):
    """Base error."""


class CannotConnect(EtsyShopManagerError):
    """The service could not be reached or returned an error."""


class InvalidAuth(EtsyShopManagerError):
    """The API key was rejected."""


class NotFound(EtsyShopManagerError):
    """The service doesn't know that order item (or is too old to take the call)."""


class EtsyShopManagerApi:
    """Reads shops and open orders, and marks order items made. Authenticates with an API key created in the app
    (Admin -> API keys)."""

    def __init__(self, session: aiohttp.ClientSession, url: str, api_key: str) -> None:
        """`session` decides certificate checking (async_get_clientsession(hass, verify_ssl=...))."""
        self._session = session
        self._url = normalize_url(url)
        self._api_key = api_key

    async def async_get_shops(self) -> list[dict[str, Any]]:
        """The connected shops (used to validate the connection)."""
        data = await self._request("GET", "/shops")
        return data.get("shops") or []

    async def async_get_open_orders(self) -> dict[str, Any]:
        """Every shop with its open orders ("shops", in the payload shape the etsy-orders-card reads), plus the
        order color list the app manages ("color_config"; missing from older versions of the app)."""
        return await self._request("GET", "/open-orders")

    async def async_set_made(self, receipt_id: str, transaction_id: str, made: int) -> int:
        """Marks how many units of one order item are made (the card's Made checkboxes; kept in the app, never sent
        to Etsy). Returns the count the app saved, which it caps to the item's quantity."""
        data = await self._request(
            "POST", f"/orders/{int(receipt_id)}/items/{int(transaction_id)}/made", {"made": max(0, int(made))}
        )
        return int(data.get("made_quantity", made))

    async def _request(self, method: str, path: str, body: dict[str, Any] | None = None) -> dict[str, Any]:
        try:
            # "async with" releases the connection on every path, including the error replies below.
            async with asyncio.timeout(30), self._session.request(
                method,
                f"{self._url}{API_PREFIX}{path}",
                headers={"Authorization": f"Bearer {self._api_key}", "Accept": "application/json"},
                json=body,
            ) as response:
                if response.status == 401:
                    raise InvalidAuth("The API key was rejected")
                if response.status == 404:
                    raise NotFound(f"HTTP 404 from {path}")
                if response.status != 200:
                    raise CannotConnect(f"HTTP {response.status} from {path}")
                return await response.json()
        except (aiohttp.ClientError, TimeoutError, ValueError) as err:
            raise CannotConnect(str(err) or err.__class__.__name__) from err


def normalize_url(url: str) -> str:
    """'https://etsy.example.lan/' -> 'https://etsy.example.lan'."""
    return url.strip().rstrip("/")
