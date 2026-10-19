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
  `image_url`, …), plus `shop_id`, `shop_name` and `orders_synced_at`. Each item also has `transaction_id` and
  `made_quantity` (units marked made in the app or on the card).
  `open_orders` and `items_needed` are kept out of the recorder (HA limits recorded attributes to 16 KB).
- An event, `etsy_shop_manager_new_order`, when an order appears that wasn't open at the previous update, and a
  matching **New order received** device trigger. Event data: `device_id`, `shop_id`, `shop_name`, `new_orders`,
  `receipts` (the new orders, same shape as `open_orders`) and `orders` (their items, flattened). Orders already
  open when Home Assistant starts are not announced.
- An action, **Mark item made** (`etsy_shop_manager.set_made`, integration 0.4.0 or later), with `receipt_id`,
  `transaction_id` and `made` (units made, 0 to unmark). It saves the mark in the app, the same mark the app's Orders
  and To make pages set, and the sensor shows it right away. Nothing is sent to Etsy. The card's Made checkboxes use it.

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

If the service is briefly unreachable (for example while it restarts after an update), the sensors keep their
last values for up to 5 minutes instead of going unavailable, so dashboards don't blank out.

## Switch the card over

`www/etsy-orders-card.js` is the card (v2.8.0); it reads the same attributes as before. Point each shop at the new sensor:

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

### Order colors

The color list (`colors`, `metallic`, `random_words`) is managed in the app under **Admin → Order colors** and reaches
the card through the sensors' `color_config` attribute (integration 0.3.0 or later). The app's Orders page colors with
the same engine, so both always match. Anything still set in the card's YAML is merged on top (YAML wins per color
name), so once the app has your list you can delete `colors`, `metallic` and `random_words` from the card config. The
look settings (`color_tint`, `metallic_shine`, `random_style`, animations) stay in the card config.

### Made checkboxes

With integration 0.4.0 or later, each item on the card has a Made checkbox (card v2.7.0). It is the same mark as on the
app's Orders and To make pages, so ticking it in either place shows in the other. For an item of several units, each
tap marks one more ("1/3", "2/3", …) and a tap on a fully made item clears it. Made items fade with their title struck
through, the shop's "To make" line counts only what's left, and tapping the checkbox doesn't open the order.
`show_made: false` hides the checkboxes.

### One list for all shops

`combine_shops: true` (card v2.8.0) shows every shop's orders in one list, most urgent first, as the app's Orders page
does, instead of a section per shop. A dropdown at the top picks **All shops** or one shop, each with its order count.
The chips and the "To make" line follow the choice. Under All shops each order names its shop, and its outline is
the shop's color. The choice is remembered on each device.

```yaml
type: custom:etsy-orders-card
combine_shops: true
shops:
  - entity: sensor.shopone_open_orders
    name: Shop One
  - entity: sensor.shoptwo_open_orders
    name: Shop Two
```

## Development

```bash
pip install pytest-homeassistant-custom-component
pytest
```

The tests run the integration in a real Home Assistant against a mocked service (config flow, sensors, new-order
events, re-authentication, options). On Windows, `tests/conftest.py` adapts HA's Linux-oriented test setup, and
HA's cleanup check reports one lingering shutdown thread on the first test; the tests themselves pass.
