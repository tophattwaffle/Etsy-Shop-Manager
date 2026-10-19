"""Etsy Shop Manager: open Etsy orders from the self-hosted Etsy Shop Manager service.

Home Assistant talks only to that service (on the LAN), never to Etsy.
"""

from __future__ import annotations

from homeassistant.const import CONF_API_KEY, CONF_URL, Platform
from homeassistant.core import HomeAssistant
from homeassistant.helpers import config_validation as cv
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.typing import ConfigType

from .api import EtsyShopManagerApi
from .const import CONF_VERIFY_SSL, DOMAIN, scan_interval
from .coordinator import EtsyShopManagerConfigEntry, EtsyShopManagerCoordinator
from .services import async_setup_services

PLATFORMS = [Platform.SENSOR]

CONFIG_SCHEMA = cv.config_entry_only_config_schema(DOMAIN)


async def async_setup(hass: HomeAssistant, config: ConfigType) -> bool:
    """Register the actions (set_made, used by the card's Made checkboxes)."""
    async_setup_services(hass)
    return True


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
    """Apply a new polling interval in place (no reload, so entities don't flicker)."""
    entry.runtime_data.update_interval = scan_interval(entry.options)
