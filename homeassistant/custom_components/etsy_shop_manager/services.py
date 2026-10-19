"""The set_made action: the etsy-orders-card's Made checkboxes."""

from __future__ import annotations

import voluptuous as vol

from homeassistant.config_entries import ConfigEntryState
from homeassistant.core import HomeAssistant, ServiceCall, SupportsResponse
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError

from .api import EtsyShopManagerError, InvalidAuth, NotFound
from .const import ATTR_MADE, ATTR_RECEIPT_ID, ATTR_TRANSACTION_ID, DOMAIN, SERVICE_SET_MADE
from .coordinator import EtsyShopManagerCoordinator

SET_MADE_SCHEMA = vol.Schema(
    {
        vol.Required(ATTR_RECEIPT_ID): vol.All(vol.Coerce(str), vol.Match(r"^\d+$")),
        vol.Required(ATTR_TRANSACTION_ID): vol.All(vol.Coerce(str), vol.Match(r"^\d+$")),
        vol.Required(ATTR_MADE): vol.All(vol.Coerce(int), vol.Range(min=0)),
    }
)


def async_setup_services(hass: HomeAssistant) -> None:
    """Register the integration's actions (once, for every config entry)."""

    async def set_made(call: ServiceCall) -> dict:
        receipt_id = call.data[ATTR_RECEIPT_ID]
        transaction_id = call.data[ATTR_TRANSACTION_ID]
        coordinator = _coordinator_for(hass, receipt_id)
        try:
            made = await coordinator.api.async_set_made(receipt_id, transaction_id, call.data[ATTR_MADE])
        except NotFound as err:
            raise ServiceValidationError(
                f"Etsy Shop Manager has no item {transaction_id} on order {receipt_id} (or needs updating)"
            ) from err
        except InvalidAuth as err:
            raise HomeAssistantError("The Etsy Shop Manager API key was rejected") from err
        except EtsyShopManagerError as err:
            raise HomeAssistantError(f"Can't reach Etsy Shop Manager: {err}") from err
        coordinator.async_set_item_made(receipt_id, transaction_id, made)
        return {ATTR_RECEIPT_ID: receipt_id, ATTR_TRANSACTION_ID: transaction_id, "made_quantity": made}

    hass.services.async_register(
        DOMAIN, SERVICE_SET_MADE, set_made, schema=SET_MADE_SCHEMA, supports_response=SupportsResponse.OPTIONAL
    )


def _coordinator_for(hass: HomeAssistant, receipt_id: str) -> EtsyShopManagerCoordinator:
    """The loaded entry whose shops have this order open, else the first loaded entry (it may have just closed)."""
    coordinators = [
        entry.runtime_data
        for entry in hass.config_entries.async_entries(DOMAIN)
        if entry.state is ConfigEntryState.LOADED
    ]
    if not coordinators:
        raise ServiceValidationError("Etsy Shop Manager isn't set up")
    for coordinator in coordinators:
        if coordinator.has_receipt(receipt_id):
            return coordinator
    return coordinators[0]
