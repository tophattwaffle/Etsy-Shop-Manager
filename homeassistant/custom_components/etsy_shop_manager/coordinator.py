"""Polls the Etsy Shop Manager service and announces new orders."""

from __future__ import annotations

import logging
from typing import Any

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ConfigEntryAuthFailed
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed

from .api import CannotConnect, EtsyShopManagerApi, InvalidAuth
from .const import DOMAIN, EVENT_NEW_ORDER, scan_interval

_LOGGER = logging.getLogger(__name__)

def shop_device_identifier(entry: ConfigEntry, shop_id: str) -> tuple[str, str]:
    """Device registry identifier for one shop of this config entry."""
    return (DOMAIN, f"{entry.entry_id}_{shop_id}")


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

    async def _async_update_data(self) -> dict[str, dict[str, Any]]:
        try:
            shops = await self.api.async_get_open_orders()
        except InvalidAuth as err:
            raise ConfigEntryAuthFailed("The Etsy Shop Manager API key was rejected") from err
        except CannotConnect as err:
            raise UpdateFailed(f"Can't reach Etsy Shop Manager: {err}") from err

        data = {str(shop["shop_id"]): shop for shop in shops if shop.get("shop_id") is not None}
        self._announce_new_orders(data)
        return data

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
            device = registry.async_get_device(identifiers={shop_device_identifier(self.config_entry, shop_id)})
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


EtsyShopManagerConfigEntry = ConfigEntry[EtsyShopManagerCoordinator]
