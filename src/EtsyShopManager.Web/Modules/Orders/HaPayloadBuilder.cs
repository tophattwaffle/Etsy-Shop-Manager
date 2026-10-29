using System.Globalization;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>
/// Builds the open-orders payload for Home Assistant in exactly the shape the old <c>etsyapp</c> integration put on
/// its Open Orders sensor (ported from its <c>utils.py</c> and <c>sensor.py</c>), so the existing card works unchanged.
/// Optional keys are omitted rather than null, as before, so cards can test for their presence.
/// </summary>
public static class HaPayloadBuilder
{
    /// <summary>Etsy puts buyer personalization in the variations, tagged with this property id.</summary>
    public const long PersonalizationPropertyId = 54;

    public static JsonObject OpenOrders(IEnumerable<Order> orders, TimeZoneInfo timeZone)
    {
        var openList = orders.Where(o => o.IsOpen).ToList();
        // Newest first before the (stable) ship-by sort, matching the old integration's order for ties.
        var summaries = openList
            .OrderByDescending(o => o.EtsyCreatedAtUtc)
            .Select(o => ReceiptSummary(o, timeZone))
            .OrderBy(s => s["ship_by_ts"] is null)
            .ThenBy(s => (long?)s["ship_by_ts"] ?? 0)
            .ToList();
        var itemsNeeded = AggregateItemsNeeded(summaries);

        var addressAlerts = new JsonArray();
        foreach (var order in openList.Where(o => o.HasActiveAddressAlert))
        {
            var isSuggestion = order.AddressCheckStatus == AddressCheckStatuses.SuggestedAddress;
            addressAlerts.Add(new JsonObject
            {
                ["receipt_id"] = order.EtsyReceiptId.ToString(CultureInfo.InvariantCulture),
                ["order_id"] = order.Id,
                ["buyer_name"] = order.BuyerName ?? "",
                ["status"] = order.AddressCheckStatus,
                ["message"] = order.AddressCheckAlert,
                ["is_suggestion"] = isSuggestion,
                ["suggested_address"] = order.AddressCheckSuggested,
            });
        }

        var personalizationAlerts = new JsonArray();
        foreach (var order in openList.Where(o => o.HasActivePersonalizationAlert))
        {
            personalizationAlerts.Add(new JsonObject
            {
                ["receipt_id"] = order.EtsyReceiptId.ToString(CultureInfo.InvariantCulture),
                ["order_id"] = order.Id,
                ["buyer_name"] = order.BuyerName ?? "",
                ["status"] = order.PersonalizationCheckStatus ?? "Failed",
                ["message"] = order.PersonalizationCheckAlert ?? "",
            });
        }

        return new JsonObject
        {
            ["open_orders_count"] = summaries.Count,
            ["total_items_needed"] = itemsNeeded.Sum(i => (int)i["quantity"]!),
            ["items_needed"] = new JsonArray([.. itemsNeeded]),
            ["open_orders"] = new JsonArray([.. summaries]),
            ["address_alerts"] = addressAlerts,
            ["address_alert_css"] = AddressAlertCss,
            ["personalization_alerts"] = personalizationAlerts,
            ["personalization_alert_css"] = PersonalizationAlertCss,
        };
    }

    public static JsonObject TransactionDetail(OrderItem item, Order order, TimeZoneInfo timeZone)
    {
        var variations = new JsonArray();
        var personalization = new List<string>();
        // The seller's changes (OrderOverrides) stand in for the buyer's choices, so the card shows what to make.
        var all = OrderOverrides.Variations(item);
        foreach (var v in all)
        {
            if (v.PropertyId == PersonalizationPropertyId)
            {
                // Keep the seller's question label when it says more than the generic "Personalization".
                if (v.Value.Length > 0)
                    personalization.Add(v.Name.Length > 0 && !v.Name.Trim().Equals("personalization", StringComparison.OrdinalIgnoreCase)
                        ? $"{v.Name}: {v.Value}"
                        : v.Value);
                continue;
            }
            variations.Add(new JsonObject { ["property"] = v.Name, ["value"] = v.Value });
        }

        var variationsText = string.Join(", ", all
            .Where(v => v.PropertyId != PersonalizationPropertyId && v.Value.Length > 0)
            .Select(v => $"{v.Name}: {v.Value}"));

        var detail = new JsonObject
        {
            ["transaction_id"] = item.EtsyTransactionId.ToString(CultureInfo.InvariantCulture),
            ["receipt_id"] = order.EtsyReceiptId.ToString(CultureInfo.InvariantCulture),
            ["title"] = item.Title,
            ["listing_id"] = item.EtsyListingId?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["buyer_user_id"] = order.BuyerUserId.ToString(CultureInfo.InvariantCulture),
            ["quantity"] = item.Quantity,
            ["price_amount"] = Amount(item.PriceAmount, item.PriceDivisor),
            ["price_currency"] = item.CurrencyCode ?? "USD",
            ["variations"] = variations,
            ["variations_text"] = variationsText,
            ["created_date"] = Format(item.EtsyCreatedAtUtc, timeZone),
            ["updated_date"] = null,
            ["expected_ship_ts"] = Unix(item.ExpectedShipAtUtc),
            // Units marked made in the app or on the card (the card's Made checkboxes); added after the old integration.
            ["made_quantity"] = item.UnitsMade,
        };
        if (personalization.Count > 0) detail["personalization"] = string.Join(" | ", personalization);
        if (!string.IsNullOrEmpty(item.ImageUrl)) detail["image_url"] = item.ImageUrl;
        return detail;
    }

    public static JsonObject ReceiptSummary(Order order, TimeZoneInfo timeZone)
    {
        var items = order.Items.OrderBy(i => i.EtsyTransactionId).Select(i => TransactionDetail(i, order, timeZone)).ToList();

        // Items-only subtotal, kept as order_total for compatibility with existing templates.
        var itemsSubtotal = order.Items.Sum(i => Amount(i.PriceAmount, i.PriceDivisor) * Math.Max(1, i.Quantity));
        var currency = order.CurrencyCode ?? order.Items.FirstOrDefault()?.CurrencyCode ?? "USD";

        var orderDate = Format(order.EtsyCreatedAtUtc, timeZone)
                        ?? order.Items.Select(i => Format(i.EtsyCreatedAtUtc, timeZone)).Where(d => d is not null).Min();
        var shipBy = order.Items.Select(i => Unix(i.ExpectedShipAtUtc)).Where(t => t is not null).Min();

        var summary = new JsonObject
        {
            ["receipt_id"] = order.EtsyReceiptId.ToString(CultureInfo.InvariantCulture),
            ["buyer_user_id"] = order.BuyerUserId.ToString(CultureInfo.InvariantCulture),
            ["order_total"] = Math.Round(itemsSubtotal, 2),
            ["currency_code"] = currency,
            ["order_date"] = orderDate,
            ["ship_by_ts"] = shipBy,
            ["ship_by_date"] = shipBy is { } ts ? Format(DateTime.UnixEpoch.AddSeconds(ts), timeZone) : null,
            ["item_count"] = items.Count,
            ["items"] = new JsonArray([.. items]),
            ["status"] = order.Status,
            ["is_paid"] = order.IsPaid,
            ["is_shipped"] = order.IsShipped,
        };
        if (!string.IsNullOrEmpty(order.BuyerName)) summary["buyer_name"] = order.BuyerName;
        var note = OrderOverrides.Note(order);
        if (!string.IsNullOrEmpty(note)) summary["message_from_buyer"] = note;
        if (order.IsGift)
        {
            summary["is_gift"] = true;
            if (!string.IsNullOrEmpty(order.GiftMessage)) summary["gift_message"] = order.GiftMessage;
        }
        if (order.Subtotal is { } sub) summary["subtotal"] = Amount(sub, order.Divisor);
        if (order.Grandtotal is { } grand) summary["grandtotal"] = Amount(grand, order.Divisor);
        if (order.ShippingCost is { } ship) summary["total_shipping_cost"] = Amount(ship, order.Divisor);
        if (order.TaxCost is { } tax) summary["total_tax_cost"] = Amount(tax, order.Divisor);
        if (order.DiscountAmount is { } discount && discount != 0) summary["discount_amount"] = Amount(discount, order.Divisor);
        if (order.HasActiveAddressAlert)
        {
            var isSuggestion = order.AddressCheckStatus == AddressCheckStatuses.SuggestedAddress;
            summary["address_alert"] = new JsonObject
            {
                ["status"] = order.AddressCheckStatus,
                ["severity"] = isSuggestion ? "warning" : "error",
                ["message"] = order.AddressCheckAlert,
                ["is_suggestion"] = isSuggestion,
                ["suggested_address"] = order.AddressCheckSuggested,
            };
        }
        if (order.HasActivePersonalizationAlert)
        {
            summary["personalization_alert"] = new JsonObject
            {
                ["status"] = order.PersonalizationCheckStatus ?? "Failed",
                ["severity"] = "error",
                ["message"] = order.PersonalizationCheckAlert,
            };
        }
        return summary;
    }

    /// <summary>
    /// One row per distinct item still to make across open orders: same listing, same variations and same
    /// personalization are summed; the row keeps the most urgent ship-by. Most needed first, then by title.
    /// </summary>
    public static List<JsonObject> AggregateItemsNeeded(IEnumerable<JsonObject> receiptSummaries)
    {
        var rows = new Dictionary<string, JsonObject>();
        foreach (var summary in receiptSummaries)
        foreach (var node in summary["items"]!.AsArray())
        {
            var item = node!.AsObject();
            var variations = item["variations"]!.AsArray();
            var personalization = (string?)item["personalization"];
            var shipTs = (long?)item["expected_ship_ts"];
            var key = ItemKey(item);
            var quantity = (int?)item["quantity"] is int q && q > 0 ? q : 1;

            if (rows.TryGetValue(key, out var row))
            {
                row["quantity"] = (int)row["quantity"]! + quantity;
                var current = (long?)row["ship_by_ts"];
                if (shipTs is not null && (current is null || shipTs < current)) row["ship_by_ts"] = shipTs;
            }
            else
            {
                row = new JsonObject
                {
                    ["title"] = (string?)item["title"],
                    ["listing_id"] = (string?)item["listing_id"] ?? "",
                    ["variations"] = variations.DeepClone(),
                    ["variations_text"] = (string?)item["variations_text"] ?? "",
                    ["quantity"] = quantity,
                };
                if (!string.IsNullOrEmpty(personalization)) row["personalization"] = personalization;
                if (shipTs is not null) row["ship_by_ts"] = shipTs;
                rows[key] = row;
            }
        }
        return rows.Values
            .OrderByDescending(r => (int)r["quantity"]!)
            .ThenBy(r => (string?)r["title"] ?? "", StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>What makes two items the same thing to make: same listing, variations and personalization.</summary>
    public static string ItemKey(JsonObject transactionDetail) =>
        $"{(string?)transactionDetail["listing_id"]}\u001f{transactionDetail["variations"]!.ToJsonString()}\u001f{(string?)transactionDetail["personalization"]}";

    private static double Amount(long amount, int divisor) =>
        Math.Round((double)amount / (divisor > 0 ? divisor : 100), 2);

    private static long? Unix(DateTime? utc) =>
        utc is { } d ? (long)(DateTime.SpecifyKind(d, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds : null;

    /// <summary>"yyyy-MM-dd HH:mm:ss" in the host's time zone, as the old integration formatted in HA's.</summary>
    private static string? Format(DateTime? utc, TimeZoneInfo timeZone) =>
        utc is { } d
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(d, DateTimeKind.Utc), timeZone)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : null;

    /// <summary>Shared CSS rules for address problem banners and alerts across the HA card and payload.</summary>
    public const string AddressAlertCss = """
/* Address Alerts in Home Assistant Card and App */
.address-alerts-blurb {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-radius: 10px;
  background: rgba(219, 68, 55, 0.15);
  border: 1px solid rgba(219, 68, 55, 0.4);
  color: var(--primary-text-color, inherit);
  font-size: 13px;
  line-height: 1.4;
  margin-bottom: 8px;
}
.address-alerts-blurb .blurb-icon { font-size: 16px; flex: none; }
.address-alerts-blurb .blurb-text { flex: 1; }
.address-alert {
  margin: 4px 0 6px 0;
  padding: 6px 10px;
  border-radius: 8px;
  font-size: 12px;
  line-height: 1.4;
  display: flex;
  gap: 8px;
  align-items: flex-start;
}
.address-alert.error {
  background: rgba(219, 68, 55, 0.12);
  border-left: 3px solid #db4437;
  color: var(--primary-text-color, inherit);
}
.address-alert.suggestion {
  background: rgba(255, 167, 38, 0.12);
  border-left: 3px solid #ffa726;
  color: var(--primary-text-color, inherit);
}
.address-alert .alert-icon { flex: none; font-size: 14px; }
.address-alert .alert-body { flex: 1; min-width: 0; }
.address-alert .alert-msg { font-weight: 500; }
.address-alert .alert-suggested { margin-top: 2px; opacity: 0.9; }
""";

    public const string PersonalizationAlertCss = """
/* Personalization Alerts in Home Assistant Card and App */
.personalization-alerts-blurb {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-radius: 10px;
  background: rgba(219, 68, 55, 0.15);
  border: 1px solid rgba(219, 68, 55, 0.4);
  color: var(--primary-text-color, inherit);
  font-size: 13px;
  line-height: 1.4;
  margin-bottom: 8px;
}
.personalization-alerts-blurb .blurb-icon { font-size: 16px; flex: none; }
.personalization-alerts-blurb .blurb-text { flex: 1; }
.personalization-alert {
  margin: 4px 0 6px 0;
  padding: 6px 10px;
  border-radius: 8px;
  font-size: 12px;
  line-height: 1.4;
  display: flex;
  gap: 8px;
  align-items: flex-start;
  background: rgba(219, 68, 55, 0.12);
  border-left: 3px solid #db4437;
  color: var(--primary-text-color, inherit);
}
.personalization-alert .alert-icon { flex: none; font-size: 14px; }
.personalization-alert .alert-body { flex: 1; min-width: 0; }
.personalization-alert .alert-msg { font-weight: 500; }
""";
}
