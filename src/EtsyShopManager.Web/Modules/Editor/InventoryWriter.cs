using System.Text.Json.Nodes;
using EtsyShopManager.Etsy.Models;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>
/// Builds the body for updateListingInventory. Etsy replaces the whole inventory on every write, so the body always
/// starts from a fresh read of Etsy's inventory and reproduces every product, property value, SKU, enabled flag,
/// price, quantity and processing profile exactly; callers change only what they mean to.
/// </summary>
public static class InventoryWriter
{
    /// <param name="adjust">Called per offering with its product; return the offering to send (default: unchanged).</param>
    public static EtsyInventoryUpdate FromEtsy(EtsyInventory inventory,
        Func<EtsyProduct, EtsyOffering, EtsyOfferingUpdate, EtsyOfferingUpdate>? adjust = null) => new()
    {
        PriceOnProperty = [.. inventory.PriceOnProperty ?? []],
        QuantityOnProperty = [.. inventory.QuantityOnProperty ?? []],
        SkuOnProperty = [.. inventory.SkuOnProperty ?? []],
        ReadinessStateOnProperty = [.. inventory.ReadinessStateOnProperty ?? []],
        Products = [.. inventory.Products.Where(p => !p.IsDeleted).Select(p => new EtsyProductUpdate
        {
            Sku = p.Sku ?? "",
            PropertyValues = [.. p.PropertyValues.Select(v => new EtsyPropertyValueUpdate
            {
                PropertyId = v.PropertyId,
                PropertyName = v.PropertyName,
                ScaleId = v.ScaleId,
                ValueIds = [.. v.ValueIds ?? []],
                Values = [.. v.Values ?? []],
            })],
            Offerings = [.. p.Offerings.Where(o => !o.IsDeleted).Select(o =>
            {
                var update = new EtsyOfferingUpdate
                {
                    Price = Price(o.Price),
                    Quantity = o.Quantity,
                    IsEnabled = o.IsEnabled,
                    ReadinessStateId = o.ReadinessStateId,
                };
                return adjust?.Invoke(p, o, update) ?? update;
            })],
        })],
    };

    public static decimal Price(EtsyMoney? money) =>
        money is null ? 0 : money.Divisor > 0 ? (decimal)money.Amount / money.Divisor : money.Amount;

    /// <summary>Identifies a product by its variation values (Etsy issues new product ids on every inventory write).</summary>
    public static string ProductKey(IEnumerable<EtsyPropertyValue> values) =>
        string.Join(";", values.OrderBy(v => v.PropertyId)
            .Select(v => $"{v.PropertyId}:{v.ScaleId}:{string.Join(",", v.ValueIds ?? [])}:{string.Join("|", v.Values ?? [])}"));

    /// <summary>The same key from a product in managed-state JSON (see ManagedListingState / ManagedProperty).</summary>
    public static string ProductKey(JsonNode? product) =>
        string.Join(";", (product?["property_values"]?.AsArray() ?? [])
            .Select(v => v!.AsObject())
            .OrderBy(v => (long)v["property_id"]!)
            .Select(v => $"{(long)v["property_id"]!}:{(long?)v["scale_id"]}:" +
                         $"{string.Join(",", v["value_ids"]!.AsArray().Select(x => (long)x!))}:" +
                         $"{string.Join("|", v["values"]!.AsArray().Select(x => (string?)x))}"));

    /// <summary>
    /// The inventory to send for a managed-state inventory (the local, edited version): its variations, combinations,
    /// prices, processing, availability, SKUs and what varies by what, on top of a fresh read of Etsy's inventory.
    /// Combinations Etsy already has (matched by their variation values) keep Etsy's stock and order; new ones follow
    /// them and start with <see cref="NewCombinationStock"/>. Stock is never changed for existing combinations.
    /// </summary>
    public static EtsyInventoryUpdate FromLocal(EtsyInventory etsy, JsonNode localInventory)
    {
        var etsyProducts = etsy.Products.Where(p => !p.IsDeleted).ToList();
        var etsyByKey = etsyProducts.GroupBy(p => ProductKey(p.PropertyValues)).ToDictionary(g => g.Key, g => g.First());
        var local = (localInventory["products"]?.AsArray() ?? []).Select(p => p!).ToList();
        var newStock = NewCombinationStock(etsy);
        // Etsy's order of the variations is the order buyers pick them in; managed state sorts them by id. A new one goes last.
        var propertyOrder = etsyProducts.SelectMany(p => p.PropertyValues).Select(v => v.PropertyId).Distinct().ToList();
        int Position(JsonNode? v) => propertyOrder.IndexOf((long)v!["property_id"]!) is var at and >= 0 ? at : propertyOrder.Count;

        var hasCustomValueOrder = localInventory["value_order"] is JsonObject vo && vo.Any(kv => kv.Value is JsonArray arr && arr.Count > 0);
        var etsyOrder = etsyProducts.Select((p, i) => (Key: ProductKey(p.PropertyValues), i)).GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.First().i);

        // Order products following the local product grid sequence when custom value_order is present,
        // otherwise retain Etsy's product order.
        var ordered = hasCustomValueOrder
            ? local.Select((p, i) => (Product: p, Key: ProductKey(p), i)).ToList()
            : local.Select((p, i) => (Product: p, Key: ProductKey(p), i))
                .OrderBy(x => etsyOrder.TryGetValue(x.Key, out var at) ? at : etsyProducts.Count + x.i)
                .ToList();

        return new EtsyInventoryUpdate
        {
            PriceOnProperty = Longs(localInventory["price_on_property"]),
            QuantityOnProperty = Longs(localInventory["quantity_on_property"]),
            SkuOnProperty = Longs(localInventory["sku_on_property"]),
            ReadinessStateOnProperty = Longs(localInventory["readiness_state_on_property"]),
            Products = [.. ordered.Select(x =>
            {
                etsyByKey.TryGetValue(x.Key, out var existing);
                return new EtsyProductUpdate
                {
                    Sku = (string?)x.Product["sku"] ?? "",
                    PropertyValues = [.. (x.Product["property_values"]?.AsArray() ?? []).OrderBy(Position).Select(v => new EtsyPropertyValueUpdate
                    {
                        PropertyId = (long)v!["property_id"]!,
                        PropertyName = (string?)v["property_name"]
                                       ?? existing?.PropertyValues.FirstOrDefault(e => e.PropertyId == (long)v["property_id"]!)?.PropertyName,
                        ScaleId = (long?)v["scale_id"],
                        ValueIds = Longs(v["value_ids"]),
                        Values = [.. (v["values"]?.AsArray() ?? []).Select(s => (string)s!)],
                    })],
                    Offerings = [.. (x.Product["offerings"]?.AsArray() ?? []).Select((o, i) =>
                    {
                        var divisor = (int?)o!["price_divisor"] is int d && d > 0 ? d : 100;
                        return new EtsyOfferingUpdate
                        {
                            Price = (decimal)(long)o["price_amount"]! / divisor,
                            Quantity = existing?.Offerings.Where(e => !e.IsDeleted).ElementAtOrDefault(i)?.Quantity ?? newStock,
                            IsEnabled = (bool?)o["is_enabled"] ?? true,
                            ReadinessStateId = (long?)o["readiness_state_id"],
                        };
                    })],
                };
            })],
        };
    }

    /// <summary>
    /// The stock a new combination starts with: with one shared stock (the usual case) that stock, so buyers see the same
    /// number on every variation; with stock per variation the lowest enabled one. At least 1.
    /// </summary>
    public static int NewCombinationStock(EtsyInventory etsy)
    {
        var enabled = etsy.Products.Where(p => !p.IsDeleted)
            .SelectMany(p => p.Offerings.Where(o => !o.IsDeleted && o.IsEnabled)).Select(o => o.Quantity).ToList();
        if (enabled.Count == 0) return 1;
        return Math.Max(1, (etsy.QuantityOnProperty ?? []).Count == 0 ? enabled.Max() : enabled.Min());
    }

    private static List<long> Longs(JsonNode? array) => [.. (array?.AsArray() ?? []).Select(x => (long)x!)];
}
