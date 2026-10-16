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


class EtsyShopManagerApi:
    """Reads shops and open orders. Authenticates with an API key created in the app (Admin -> API keys)."""

    def __init__(self, session: aiohttp.ClientSession, url: str, api_key: str) -> None:
        """`session` decides certificate checking (async_get_clientsession(hass, verify_ssl=...))."""
        self._session = session
        self._url = normalize_url(url)
        self._api_key = api_key

    async def async_get_shops(self) -> list[dict[str, Any]]:
        """The connected shops (used to validate the connection)."""
        data = await self._get("/shops")
        return data.get("shops") or []

    async def async_get_open_orders(self) -> list[dict[str, Any]]:
        """Every shop with its open orders, in the payload shape the etsy-orders-card reads."""
        data = await self._get("/open-orders")
        return data.get("shops") or []

    async def _get(self, path: str) -> dict[str, Any]:
        try:
            async with asyncio.timeout(30):
                response = await self._session.get(
                    f"{self._url}{API_PREFIX}{path}",
                    headers={"Authorization": f"Bearer {self._api_key}", "Accept": "application/json"},
                )
                if response.status == 401:
                    raise InvalidAuth("The API key was rejected")
                if response.status != 200:
                    raise CannotConnect(f"HTTP {response.status} from {path}")
                return await response.json()
        except (aiohttp.ClientError, TimeoutError, ValueError) as err:
            raise CannotConnect(str(err) or err.__class__.__name__) from err


def normalize_url(url: str) -> str:
    """'https://etsy.example.lan/' -> 'https://etsy.example.lan'."""
    return url.strip().rstrip("/")
