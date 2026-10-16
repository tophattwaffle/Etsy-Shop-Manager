# Etsy Shop Manager for Home Assistant

Shows open Etsy orders in Home Assistant from your self-hosted **Etsy Shop Manager** service. Home Assistant talks
only to that service on your LAN (with an API key); the service is the one that talks to Etsy.

It replaces the old `etsyapp` integration. The domain is different (`etsy_shop_manager`), so both can run side by
side while you switch dashboards and automations over.

## What you get

For each shop connected in the app:

- A device, and an **Open orders** sensor (`sensor.<shop>_open_orders`). Its state is the number of open
  (paid, not yet shipped) orders. Its attributes are the same ones the old `etsyapp` Open Orders sensor had, so the
  `etsy-orders-card` works unchanged:
  `open_orders_count`, `total_items_needed`, `items_needed`, `open_orders` (each with `receipt_id`, `buyer_name`,
  `ship_by_ts`, `order_total`, `currency_code`, `items[]` with `title`, `quantity`, `variations`, `personalization`,
  `image_url`, …), plus `shop_id`, `shop_name` and `orders_synced_at`.
  `open_orders` and `items_needed` are kept out of the recorder (HA limits recorded attributes to 16 KB).
- An event, `etsy_shop_manager_new_order`, when an order appears that wasn't open at the previous update, and a
  matching **New order received** device trigger. Event data: `device_id`, `shop_id`, `shop_name`, `new_orders`,
  `receipts` (the new orders, same shape as `open_orders`) and `orders` (their items, flattened). Orders already
  open when Home Assistant starts are not announced.

Shops connected in the app later get their sensor automatically at the next update.

## Install

**HACS:** HACS installs only from GitHub repositories, with `custom_components/` at the repository root. Once this
folder is published as its own GitHub repository, add it in HACS → ⋮ → Custom repositories (category
*Integration*), install **Etsy Shop Manager**, and restart Home Assistant.

**Manually:** copy `custom_components/etsy_shop_manager` into your Home Assistant `config/custom_components/`
folder and restart.

## Set up

1. In the Etsy Shop Manager app: **Admin → API keys → Create key** (name it e.g. "Home Assistant"). Copy the key
   (it starts with `esm_` and is only shown once).
2. In Home Assistant: **Settings → Devices & services → Add integration → Etsy Shop Manager**. Enter the address you
   open the app at (e.g. `https://etsy.example.lan`) and the key. Turn off *Verify the SSL certificate* only if the
   app uses a certificate Home Assistant doesn't trust.
3. Optional: **Configure** sets how often Home Assistant asks the service for open orders (default 30 s, 5–3600).
   That's cheap: it stays on the LAN. How often the *service* checks Etsy is set in the app under
   **Admin → Settings → Order polling** (default 5 minutes, as the old integration polled).

If the key is revoked in the app, Home Assistant asks for a new one (a repair/re-authenticate prompt).

## Switch the card over

`www/etsy-orders-card.js` is the existing card, unchanged. Point each shop at the new sensor:

```yaml
type: custom:etsy-orders-card
shops:
  - entity: sensor.shopone_open_orders   # was the etsyapp Open Orders sensor
    name: Shop One
  - entity: sensor.shoptwo_open_orders
    name: Shop Two
```

`order_url` overrides and every other card option keep working. Photos (`image_url`) are Etsy's own image
addresses, as before. Once the card and automations use the new sensors and event, remove the `etsyapp`
integration.

## Development

```bash
pip install pytest-homeassistant-custom-component
pytest
```

The tests run the integration in a real Home Assistant against a mocked service (config flow, sensors, new-order
events, re-authentication, options). On Windows, `tests/conftest.py` adapts HA's Linux-oriented test setup, and
HA's cleanup check reports one lingering shutdown thread on the first test; the tests themselves pass.
