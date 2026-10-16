"""Etsy Shop Manager: open Etsy orders from the self-hosted Etsy Shop Manager service.

Home Assistant talks only to that service (on the LAN), never to Etsy.
"""

from __future__ import annotations

from homeassistant.const import CONF_API_KEY, CONF_URL, Platform
from homeassistant.core import HomeAssistant
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import EtsyShopManagerApi
from .const import CONF_VERIFY_SSL
from .coordinator import EtsyShopManagerConfigEntry, EtsyShopManagerCoordinator

PLATFORMS = [Platform.SENSOR]


async def async_setup_entry(hass: HomeAssistant, entry: EtsyShopManagerConfigEntry) -> bool:
    """Set up from a config entry."""
    session = async_get_clientsession(hass, verify_ssl=entry.data.get(CONF_VERIFY_SSL, True))
    api = EtsyShopManagerApi(session, entry.data[CONF_URL], entry.data[CONF_API_KEY])

    coordinator = EtsyShopManagerCoordinator(hass, entry, api)
    await coordinator.async_config_entry_first_refresh()
    entry.runtime_data = coordinator

    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    entry.async_on_unload(entry.add_update_listener(_async_options_updated))
    return True


async def async_unload_entry(hass: HomeAssistant, entry: EtsyShopManagerConfigEntry) -> bool:
    """Unload a config entry."""
    return await hass.config_entries.async_unload_platforms(entry, PLATFORMS)


async def _async_options_updated(hass: HomeAssistant, entry: EtsyShopManagerConfigEntry) -> None:
    """Reload so a new polling interval takes effect."""
    await hass.config_entries.async_reload(entry.entry_id)
