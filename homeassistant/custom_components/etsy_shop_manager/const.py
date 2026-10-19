"""Constants for the Etsy Shop Manager integration."""

from datetime import timedelta

DOMAIN = "etsy_shop_manager"

CONF_VERIFY_SSL = "verify_ssl"
CONF_SCAN_INTERVAL = "scan_interval"

# Home Assistant only talks to the Etsy Shop Manager service on the LAN, so polling it often is cheap.
# (The service itself polls Etsy on its own schedule, set under Admin -> Settings in the app.)
DEFAULT_SCAN_INTERVAL = 30
MIN_SCAN_INTERVAL = 5
MAX_SCAN_INTERVAL = 3600

# Keep showing the last data while the service is briefly unreachable (e.g. restarting after a deploy), so
# dashboards don't blank out; after this long, entities go unavailable.
STALE_DATA_GRACE = timedelta(minutes=5)

EVENT_NEW_ORDER = f"{DOMAIN}_new_order"
TRIGGER_NEW_ORDER = "new_order"

API_PREFIX = "/api/ha/v1"

# The set_made action (the card's Made checkboxes).
SERVICE_SET_MADE = "set_made"
ATTR_RECEIPT_ID = "receipt_id"
ATTR_TRANSACTION_ID = "transaction_id"
ATTR_MADE = "made"


def scan_interval(options: dict) -> timedelta:
    """The polling interval from the entry options, clamped to the allowed range."""
    seconds = options.get(CONF_SCAN_INTERVAL, DEFAULT_SCAN_INTERVAL)
    try:
        seconds = int(seconds)
    except (TypeError, ValueError):
        seconds = DEFAULT_SCAN_INTERVAL
    return timedelta(seconds=max(MIN_SCAN_INTERVAL, min(MAX_SCAN_INTERVAL, seconds)))
