"""Open Orders sensor, one per shop."""

from __future__ import annotations

from typing import Any

from homeassistant.components.sensor import SensorEntity, SensorStateClass
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from .coordinator import EtsyShopManagerConfigEntry, EtsyShopManagerCoordinator, shop_device_identifier

# Passed through from the service unchanged: the shape the etsy-orders-card (and the old etsyapp sensor) uses.
_PAYLOAD_ATTRIBUTES = ("open_orders_count", "total_items_needed", "items_needed", "open_orders")


async def async_setup_entry(
    hass: HomeAssistant,
    entry: EtsyShopManagerConfigEntry,
    async_add_entities: AddEntitiesCallback,
) -> None:
    """Add a sensor per shop now, and for shops connected in the app later."""
    coordinator = entry.runtime_data
    added: set[str] = set()

    @callback
    def add_new_shops() -> None:
        new = [shop_id for shop_id in coordinator.data or {} if shop_id not in added]
        if new:
            added.update(new)
            async_add_entities(EtsyOpenOrdersSensor(coordinator, shop_id) for shop_id in new)

    add_new_shops()
    entry.async_on_unload(coordinator.async_add_listener(add_new_shops))


class EtsyOpenOrdersSensor(CoordinatorEntity[EtsyShopManagerCoordinator], SensorEntity):
    """Number of open (paid, unshipped) orders; the orders themselves are in the attributes."""

    _attr_has_entity_name = True
    _attr_translation_key = "open_orders"
    _attr_state_class = SensorStateClass.MEASUREMENT
    _attr_native_unit_of_measurement = "orders"
    # The order lists can be large; HA's recorder caps attributes at 16 KB, so keep them out of history.
    _unrecorded_attributes = frozenset({"open_orders", "items_needed", "color_config"})

    def __init__(self, coordinator: EtsyShopManagerCoordinator, shop_id: str) -> None:
        super().__init__(coordinator)
        self._shop_id = shop_id
        entry = coordinator.config_entry
        shop = self._shop or {}
        self._attr_unique_id = f"{entry.entry_id}_{shop_id}_open_orders"
        self._attr_device_info = DeviceInfo(
            identifiers={shop_device_identifier(entry, shop_id)},
            name=shop.get("shop_name") or f"Etsy shop {shop_id}",
            manufacturer="Etsy",
            model="Etsy shop",
            configuration_url=shop.get("url"),
        )

    @property
    def _shop(self) -> dict[str, Any] | None:
        return (self.coordinator.data or {}).get(self._shop_id)

    @property
    def available(self) -> bool:
        return super().available and self._shop is not None

    @property
    def native_value(self) -> int | None:
        shop = self._shop
        return None if shop is None else shop.get("open_orders_count", 0)

    @property
    def icon(self) -> str:
        count = self.native_value or 0
        if count == 0:
            return "mdi:package-variant-closed-remove"
        return f"mdi:numeric-{count}-circle" if count < 10 else "mdi:numeric-9-plus-circle"

    @property
    def extra_state_attributes(self) -> dict[str, Any]:
        shop = self._shop or {}
        attributes = {key: shop.get(key) for key in _PAYLOAD_ATTRIBUTES}
        attributes["shop_id"] = self._shop_id
        attributes["shop_name"] = shop.get("shop_name")
        attributes["orders_synced_at"] = shop.get("orders_synced_at")
        # The app's order color list; the etsy-orders-card colors variations and notes with it.
        if self.coordinator.color_config is not None:
            attributes["color_config"] = self.coordinator.color_config
        return attributes
