"""Config flow: the service URL and an API key created in the app (Admin -> API keys)."""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any

import voluptuous as vol

from homeassistant.config_entries import ConfigEntry, ConfigFlow, ConfigFlowResult, OptionsFlow
from homeassistant.const import CONF_API_KEY, CONF_URL
from homeassistant.core import callback
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import CannotConnect, EtsyShopManagerApi, InvalidAuth, normalize_url
from .const import (
    CONF_SCAN_INTERVAL,
    CONF_VERIFY_SSL,
    DEFAULT_SCAN_INTERVAL,
    DOMAIN,
    MAX_SCAN_INTERVAL,
    MIN_SCAN_INTERVAL,
)


class EtsyShopManagerConfigFlow(ConfigFlow, domain=DOMAIN):
    """Connect Home Assistant to an Etsy Shop Manager service."""

    VERSION = 1

    async def async_step_user(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        if user_input is not None:
            url = normalize_url(user_input[CONF_URL])
            await self.async_set_unique_id(url.lower())
            self._abort_if_unique_id_configured()
            error, shops = await self._async_validate(url, user_input[CONF_API_KEY], user_input[CONF_VERIFY_SSL])
            if error:
                errors["base"] = error
            else:
                title = "Etsy Shop Manager" if len(shops) != 1 else f"Etsy Shop Manager ({shops[0].get('shop_name')})"
                return self.async_create_entry(title=title, data={**user_input, CONF_URL: url})

        return self.async_show_form(
            step_id="user",
            data_schema=self.add_suggested_values_to_schema(
                vol.Schema(
                    {
                        vol.Required(CONF_URL): str,
                        vol.Required(CONF_API_KEY): str,
                        vol.Optional(CONF_VERIFY_SSL, default=True): bool,
                    }
                ),
                user_input or {},
            ),
            errors=errors,
        )

    async def async_step_reauth(self, entry_data: Mapping[str, Any]) -> ConfigFlowResult:
        """The API key was rejected (e.g. revoked in the app)."""
        return await self.async_step_reauth_confirm()

    async def async_step_reauth_confirm(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        entry = self._get_reauth_entry()
        errors: dict[str, str] = {}
        if user_input is not None:
            error, _ = await self._async_validate(
                entry.data[CONF_URL], user_input[CONF_API_KEY], entry.data.get(CONF_VERIFY_SSL, True)
            )
            if error:
                errors["base"] = error
            else:
                return self.async_update_reload_and_abort(entry, data_updates={CONF_API_KEY: user_input[CONF_API_KEY]})

        return self.async_show_form(
            step_id="reauth_confirm",
            data_schema=vol.Schema({vol.Required(CONF_API_KEY): str}),
            description_placeholders={"url": entry.data[CONF_URL]},
            errors=errors,
        )

    async def _async_validate(self, url: str, api_key: str, verify_ssl: bool) -> tuple[str | None, list[dict]]:
        api = EtsyShopManagerApi(async_get_clientsession(self.hass, verify_ssl=verify_ssl), url, api_key)
        try:
            return None, await api.async_get_shops()
        except InvalidAuth:
            return "invalid_auth", []
        except CannotConnect:
            return "cannot_connect", []

    @staticmethod
    @callback
    def async_get_options_flow(config_entry: ConfigEntry) -> OptionsFlow:
        return EtsyShopManagerOptionsFlow()


class EtsyShopManagerOptionsFlow(OptionsFlow):
    """How often Home Assistant asks the service for open orders."""

    async def async_step_init(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        if user_input is not None:
            return self.async_create_entry(data=user_input)

        current = self.config_entry.options.get(CONF_SCAN_INTERVAL, DEFAULT_SCAN_INTERVAL)
        return self.async_show_form(
            step_id="init",
            data_schema=vol.Schema(
                {
                    vol.Required(CONF_SCAN_INTERVAL, default=current): vol.All(
                        vol.Coerce(int), vol.Range(min=MIN_SCAN_INTERVAL, max=MAX_SCAN_INTERVAL)
                    ),
                }
            ),
        )
