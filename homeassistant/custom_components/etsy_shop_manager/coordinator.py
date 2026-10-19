"""Polls the Etsy Shop Manager service and announces new orders."""

from __future__ import annotations

import copy
import logging
from typing import Any

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import ConfigEntryAuthFailed
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed
from homeassistant.util import dt as dt_util

from .api import CannotConnect, EtsyShopManagerApi, InvalidAuth
from .const import DOMAIN, EVENT_NEW_ORDER, STALE_DATA_GRACE, scan_interval

_LOGGER = logging.getLogger(__name__)

def shop_device_identifier(entry: ConfigEntry, shop_id: str) -> tuple[str, str]:
    """Device registry identifier for one shop of this config entry."""
    return (DOMAIN, f"{entry.entry_id}_{shop_id}")


def find_shop_device(registry: dr.DeviceRegistry, entry: ConfigEntry, shop_id: str) -> dr.DeviceEntry | None:
    """The shop's device. HA 2026.10 looks devices up per config entry (async_get_device is deprecated and stops
    working in 2027.8); older versions only have async_get_device."""
    identifier = shop_device_identifier(entry, shop_id)
    if hasattr(registry, "async_get_device_by_identifier"):
        return registry.async_get_device_by_identifier(identifier, entry.entry_id)
    return registry.async_get_device(identifiers={identifier})


class EtsyShopManagerCoordinator(DataUpdateCoordinator[dict[str, dict[str, Any]]]):
    """Fetches every shop's open orders in one request. Data is keyed by Etsy shop id."""

    config_entry: EtsyShopManagerConfigEntry

    def __init__(self, hass: HomeAssistant, entry: EtsyShopManagerConfigEntry, api: EtsyShopManagerApi) -> None:
        super().__init__(
            hass,
            _LOGGER,
            config_entry=entry,
            name=DOMAIN,
            update_interval=scan_interval(entry.options),
            always_update=False,
        )
        self.api = api
        # Receipt ids seen per shop; None until the first successful poll, so a restart doesn't
        # announce every open order as new.
        self._known_receipts: dict[str, set[str]] | None = None
        self._last_success = None
        # The order color list from the app (colors, metallic, random_words), passed to the card unchanged.
        self.color_config: dict[str, Any] | None = None

    async def _async_update_data(self) -> dict[str, dict[str, Any]]:
        try:
            payload = await self.api.async_get_open_orders()
        except InvalidAuth as err:
            raise ConfigEntryAuthFailed("The Etsy Shop Manager API key was rejected") from err
        except CannotConnect as err:
            if self.data is not None and self._last_success and dt_util.utcnow() - self._last_success < STALE_DATA_GRACE:
                _LOGGER.warning("Etsy Shop Manager is unreachable (%s); keeping the last data for now", err)
                return self.data
            raise UpdateFailed(f"Can't reach Etsy Shop Manager: {err}") from err

        shops = payload.get("shops") or []
        data = {str(shop["shop_id"]): shop for shop in shops if shop.get("shop_id") is not None}
        color_config = payload.get("color_config") or None
        if color_config != self.color_config:
            self.color_config = color_config
            # always_update=False skips listeners when the orders are unchanged, so tell them about the colors.
            if data == self.data:
                self.async_update_listeners()
        self._last_success = dt_util.utcnow()
        self._announce_new_orders(data)
        return data

    def has_receipt(self, receipt_id: str) -> bool:
        """Whether one of this entry's shops has that order open."""
        return any(
            str(order.get("receipt_id")) == receipt_id
            for shop in (self.data or {}).values()
            for order in shop.get("open_orders") or []
        )

    @callback
    def async_set_item_made(self, receipt_id: str, transaction_id: str, made: int) -> None:
        """Show an item marked made at once (the app has saved it), without waiting for the next poll."""
        updated = with_item_made(self.data or {}, receipt_id, transaction_id, made)
        if updated is not None:
            self.async_set_updated_data(updated)

    def _announce_new_orders(self, data: dict[str, dict[str, Any]]) -> None:
        current = {
            shop_id: {str(o.get("receipt_id")) for o in shop.get("open_orders") or []}
            for shop_id, shop in data.items()
        }
        previous, self._known_receipts = self._known_receipts, current
        if previous is None:
            return

        registry = dr.async_get(self.hass)
        for shop_id, shop in data.items():
            if shop_id not in previous:
                continue  # a shop added since the last poll: its orders aren't new
            new_ids = current[shop_id] - previous[shop_id]
            if not new_ids:
                continue
            receipts = [o for o in shop.get("open_orders") or [] if str(o.get("receipt_id")) in new_ids]
            device = find_shop_device(registry, self.config_entry, shop_id)
            _LOGGER.debug("New order(s) for %s: %s", shop.get("shop_name"), sorted(new_ids))
            self.hass.bus.async_fire(
                EVENT_NEW_ORDER,
                {
                    "device_id": device.id if device else None,
                    "shop_id": shop_id,
                    "shop_name": shop.get("shop_name"),
                    "new_orders": len(receipts),
                    "receipts": receipts,
                    # Flat list of the new orders' items, as the old etsyapp event carried in "orders".
                    "orders": [item for r in receipts for item in r.get("items") or []],
                },
            )


def with_item_made(
    data: dict[str, dict[str, Any]], receipt_id: str, transaction_id: str, made: int
) -> dict[str, dict[str, Any]] | None:
    """A copy of the coordinator data with one item's made_quantity set, or None when that item isn't in it."""
    for shop_id, shop in data.items():
        for oi, order in enumerate(shop.get("open_orders") or []):
            if str(order.get("receipt_id")) != receipt_id:
                continue
            for ii, item in enumerate(order.get("items") or []):
                if str(item.get("transaction_id")) == transaction_id:
                    updated = dict(data)
                    updated[shop_id] = copy.deepcopy(shop)
                    updated[shop_id]["open_orders"][oi]["items"][ii]["made_quantity"] = made
                    return updated
    return None


EtsyShopManagerConfigEntry = ConfigEntry[EtsyShopManagerCoordinator]
