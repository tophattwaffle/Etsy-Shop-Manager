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
    /// Applies the prices and processing profiles of a managed-state inventory (the local, edited version) onto Etsy's
    /// current inventory, matching products by their variation values. Quantities, SKUs and everything else stay as Etsy
    /// has them. Products missing on either side are left as Etsy has them.
    /// </summary>
    public static EtsyInventoryUpdate WithLocalPrices(EtsyInventory etsy, JsonNode localInventory)
    {
        var local = (localInventory["products"]?.AsArray() ?? [])
            .Select(p => p!)
            .GroupBy(ProductKey)
            .ToDictionary(g => g.Key, g => g.First()["offerings"]?.AsArray().FirstOrDefault());

        return FromEtsy(etsy, (product, _, update) =>
        {
            if (!local.TryGetValue(ProductKey(product.PropertyValues), out var offering) || offering is null) return update;
            var divisor = (int?)offering["price_divisor"] is int d && d > 0 ? d : 100;
            update.Price = (decimal)(long)offering["price_amount"]! / divisor;
            update.ReadinessStateId = (long?)offering["readiness_state_id"];
            return update;
        });
    }
}
