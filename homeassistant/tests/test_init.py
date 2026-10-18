"""Config flow, sensors and new-order events against a mocked service."""

from homeassistant import config_entries
from homeassistant.const import CONF_API_KEY, CONF_URL
from homeassistant.core import HomeAssistant
from homeassistant.data_entry_flow import FlowResultType
from homeassistant.helpers import device_registry as dr
from pytest_homeassistant_custom_component.common import MockConfigEntry, async_capture_events

from custom_components.etsy_shop_manager.const import CONF_SCAN_INTERVAL, CONF_VERIFY_SSL, DOMAIN, EVENT_NEW_ORDER

from .conftest import API_KEY, URL, mock_service, order, shop_payload

SHOPS = [shop_payload("42", "ShopOne", [order("1")]), shop_payload("43", "ShopTwo", [])]


async def _setup(hass: HomeAssistant, aioclient_mock, shops=SHOPS) -> MockConfigEntry:
    mock_service(aioclient_mock, shops)
    entry = MockConfigEntry(
        domain=DOMAIN, unique_id=URL, data={CONF_URL: URL, CONF_API_KEY: API_KEY, CONF_VERIFY_SSL: True}
    )
    entry.add_to_hass(hass)
    assert await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()
    return entry


async def test_config_flow_creates_entry(hass: HomeAssistant, aioclient_mock) -> None:
    mock_service(aioclient_mock, SHOPS)
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": config_entries.SOURCE_USER})
    assert result["type"] is FlowResultType.FORM

    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {CONF_URL: URL + "/", CONF_API_KEY: API_KEY, CONF_VERIFY_SSL: True}
    )
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["data"][CONF_URL] == URL
    assert aioclient_mock.mock_calls[0][3]["Authorization"] == f"Bearer {API_KEY}"


async def test_config_flow_reports_bad_key_and_unreachable(hass: HomeAssistant, aioclient_mock) -> None:
    mock_service(aioclient_mock, SHOPS, status=401)
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": config_entries.SOURCE_USER})
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {CONF_URL: URL, CONF_API_KEY: "esm_bad", CONF_VERIFY_SSL: True}
    )
    assert result["errors"] == {"base": "invalid_auth"}

    mock_service(aioclient_mock, SHOPS, status=500)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {CONF_URL: URL, CONF_API_KEY: API_KEY, CONF_VERIFY_SSL: True}
    )
    assert result["errors"] == {"base": "cannot_connect"}


async def test_sensor_per_shop_with_card_attributes(hass: HomeAssistant, aioclient_mock) -> None:
    entry = await _setup(hass, aioclient_mock)

    one = hass.states.get("sensor.shopone_open_orders")
    two = hass.states.get("sensor.shoptwo_open_orders")
    assert one is not None and two is not None
    assert one.state == "1"
    assert two.state == "0"
    assert one.attributes["open_orders"][0]["receipt_id"] == "1"
    assert one.attributes["total_items_needed"] == 1
    assert one.attributes["items_needed"][0]["title"] == "Galaxy vase"

    names = {d.name for d in dr.async_entries_for_config_entry(dr.async_get(hass), entry.entry_id)}
    assert names == {"ShopOne", "ShopTwo"}


async def test_new_order_fires_event_only_after_first_poll(hass: HomeAssistant, aioclient_mock) -> None:
    events = async_capture_events(hass, EVENT_NEW_ORDER)
    entry = await _setup(hass, aioclient_mock)
    assert events == []  # orders already open at startup are not "new"

    mock_service(
        aioclient_mock,
        [shop_payload("42", "ShopOne", [order("1"), order("2")]), shop_payload("43", "ShopTwo", [])],
    )
    await entry.runtime_data.async_refresh()
    await hass.async_block_till_done()

    assert len(events) == 1
    data = events[0].data
    assert data["shop_name"] == "ShopOne"
    assert data["new_orders"] == 1
    assert [r["receipt_id"] for r in data["receipts"]] == ["2"]
    assert data["orders"][0]["title"] == "Galaxy vase"
    device = dr.async_get(hass).async_get(data["device_id"])
    assert device is not None and device.name == "ShopOne"
    assert hass.states.get("sensor.shopone_open_orders").state == "2"


async def test_shop_added_later_gets_a_sensor(hass: HomeAssistant, aioclient_mock) -> None:
    entry = await _setup(hass, aioclient_mock, shops=[SHOPS[0]])
    assert hass.states.get("sensor.shoptwo_open_orders") is None

    mock_service(aioclient_mock, SHOPS)
    await entry.runtime_data.async_refresh()
    await hass.async_block_till_done()

    assert hass.states.get("sensor.shoptwo_open_orders").state == "0"


async def test_rejected_key_starts_reauth(hass: HomeAssistant, aioclient_mock) -> None:
    entry = await _setup(hass, aioclient_mock)
    mock_service(aioclient_mock, SHOPS, status=401)
    await entry.runtime_data.async_refresh()
    await hass.async_block_till_done()

    flows = [f for f in hass.config_entries.flow.async_progress() if f["context"]["source"] == config_entries.SOURCE_REAUTH]
    assert len(flows) == 1

    mock_service(aioclient_mock, SHOPS)
    result = await hass.config_entries.flow.async_configure(flows[0]["flow_id"], {CONF_API_KEY: "esm_new"})
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "reauth_successful"
    assert entry.data[CONF_API_KEY] == "esm_new"


async def test_options_set_polling_interval(hass: HomeAssistant, aioclient_mock) -> None:
    entry = await _setup(hass, aioclient_mock)
    result = await hass.config_entries.options.async_init(entry.entry_id)
    result = await hass.config_entries.options.async_configure(result["flow_id"], {CONF_SCAN_INTERVAL: 120})
    assert result["type"] is FlowResultType.CREATE_ENTRY
    await hass.async_block_till_done()
    assert entry.runtime_data.update_interval.total_seconds() == 120


async def test_brief_outage_keeps_last_data_then_goes_unavailable(hass: HomeAssistant, aioclient_mock, freezer) -> None:
    entry = await _setup(hass, aioclient_mock)
    mock_service(aioclient_mock, SHOPS, status=503)  # e.g. the service restarting after a deploy

    freezer.tick(60)
    await entry.runtime_data.async_refresh()
    await hass.async_block_till_done()
    state = hass.states.get("sensor.shopone_open_orders")
    assert state.state == "1"
    assert state.attributes["open_orders"][0]["receipt_id"] == "1"

    freezer.tick(600)
    await entry.runtime_data.async_refresh()
    await hass.async_block_till_done()
    assert hass.states.get("sensor.shopone_open_orders").state == "unavailable"


async def test_options_change_does_not_reload(hass: HomeAssistant, aioclient_mock) -> None:
    entry = await _setup(hass, aioclient_mock)
    coordinator = entry.runtime_data
    result = await hass.config_entries.options.async_init(entry.entry_id)
    await hass.config_entries.options.async_configure(result["flow_id"], {CONF_SCAN_INTERVAL: 45})
    await hass.async_block_till_done()
    assert entry.runtime_data is coordinator
    assert coordinator.update_interval.total_seconds() == 45


COLORS = {"colors": {"Copper": ["#B87333"]}, "metallic": ["Copper"], "random_words": ["random"], "version": "a1"}


async def test_color_list_from_the_app_reaches_every_shop_sensor(hass: HomeAssistant, aioclient_mock) -> None:
    mock_service(aioclient_mock, SHOPS, color_config=COLORS)
    entry = MockConfigEntry(
        domain=DOMAIN, unique_id=URL, data={CONF_URL: URL, CONF_API_KEY: API_KEY, CONF_VERIFY_SSL: True}
    )
    entry.add_to_hass(hass)
    assert await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()

    assert hass.states.get("sensor.shopone_open_orders").attributes["color_config"] == COLORS
    assert hass.states.get("sensor.shoptwo_open_orders").attributes["color_config"] == COLORS

    # A color edit in the app shows up even when no order changed.
    changed = {**COLORS, "colors": {"Copper": ["#B87333"], "Teal": ["#008080"]}, "version": "b2"}
    mock_service(aioclient_mock, SHOPS, color_config=changed)
    await entry.runtime_data.async_refresh()
    await hass.async_block_till_done()
    assert hass.states.get("sensor.shopone_open_orders").attributes["color_config"] == changed


async def test_older_app_without_colors_leaves_the_attribute_out(hass: HomeAssistant, aioclient_mock) -> None:
    await _setup(hass, aioclient_mock)
    assert "color_config" not in hass.states.get("sensor.shopone_open_orders").attributes
