"""Fixtures and a mocked Etsy Shop Manager service."""

import sys

import pytest

from custom_components.etsy_shop_manager.const import DOMAIN

if sys.platform == "win32":
    # HA's test plugin blocks sockets before each test, but Windows' event loop needs a local socket pair
    # internally. Skip the block on Windows; HTTP is still mocked (aioclient_mock), so nothing reaches the network.
    import asyncio

    import pytest_socket
    from homeassistant import runner

    pytest_socket.disable_socket = lambda *args, **kwargs: None
    # HA's HTTP client (aiodns) needs a selector loop; Windows defaults to the proactor loop.
    runner.HassEventLoopPolicy._loop_factory = asyncio.SelectorEventLoop

URL = "https://etsy.example.lan"
API_KEY = "esm_testkey"


@pytest.fixture(autouse=True)
def auto_enable_custom_integrations(enable_custom_integrations):
    """Load integrations from custom_components."""
    yield


def order(receipt_id: str, ship_by: int = 1760800000) -> dict:
    return {
        "receipt_id": receipt_id,
        "buyer_name": f"Buyer {receipt_id}",
        "ship_by_ts": ship_by,
        "order_total": 19.99,
        "currency_code": "USD",
        "items": [
            {
                "title": "Galaxy vase",
                "quantity": 1,
                "variations": [{"property": "Color", "value": "Black"}],
                "listing_id": "7001",
                "image_url": "https://i.etsystatic.com/x.jpg",
            }
        ],
    }


def shop_payload(shop_id: str, name: str, orders: list[dict]) -> dict:
    return {
        "shop_id": shop_id,
        "shop_name": name,
        "url": f"https://www.etsy.com/shop/{name}",
        "open_orders_count": len(orders),
        "total_items_needed": len(orders),
        "items_needed": [{"title": "Galaxy vase", "quantity": len(orders)}] if orders else [],
        "open_orders": orders,
        "orders_synced_at": 1760900000,
    }


def mock_service(aioclient_mock, shops: list[dict], status: int = 200) -> None:
    aioclient_mock.clear_requests()
    aioclient_mock.get(
        f"{URL}/api/ha/v1/shops",
        status=status,
        json={"shops": [{"shop_id": s["shop_id"], "shop_name": s["shop_name"]} for s in shops]},
    )
    aioclient_mock.get(f"{URL}/api/ha/v1/open-orders", status=status, json={"shops": shops})
