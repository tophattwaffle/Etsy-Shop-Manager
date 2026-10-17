using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>A shop's lookups as id → name, for the editor's dropdowns and for describing values in previews.</summary>
public sealed record EditorLookups(
    IReadOnlyDictionary<long, string> ShippingProfiles,
    IReadOnlyDictionary<long, string> ProcessingProfiles,
    IReadOnlyDictionary<long, string> ReturnPolicies,
    IReadOnlyDictionary<long, string> Sections,
    IReadOnlyDictionary<long, string> ProductionPartners,
    IReadOnlyDictionary<long, string> Categories,
    string? CurrencyCode)
{
    public static async Task<EditorLookups> LoadAsync(AppDbContext db, int shopId, IEnumerable<long> categoryIds, string? currency,
        CancellationToken ct = default)
    {
        var ids = categoryIds.Distinct().ToList();
        return new EditorLookups(
            await db.ShippingProfiles.Where(x => x.ShopId == shopId).OrderBy(x => x.Title).ToDictionaryAsync(x => x.EtsyShippingProfileId, x => x.Title, ct),
            await db.ProcessingProfiles.Where(x => x.ShopId == shopId).OrderBy(x => x.MinProcessingDays)
                .ToDictionaryAsync(x => x.ReadinessStateId, x => x.DisplayLabel ?? x.ReadinessState, ct),
            (await db.ReturnPolicies.Where(x => x.ShopId == shopId).ToListAsync(ct))
                .ToDictionary(x => x.EtsyReturnPolicyId, ListingQueries.ReturnPolicySummary),
            await db.ShopSections.Where(x => x.ShopId == shopId).OrderBy(x => x.Rank).ToDictionaryAsync(x => x.EtsySectionId, x => x.Title, ct),
            await db.ProductionPartners.Where(x => x.ShopId == shopId).ToDictionaryAsync(x => x.EtsyPartnerId, x => x.Name, ct),
            await db.TaxonomyNodes.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullPath, ct),
            currency);
    }

    /// <summary>A field value as people read it: names instead of ids, Yes/No, prices per variation.</summary>
    public string Describe(string key, JsonNode? value)
    {
        if (value is null) return "—";
        string Name(IReadOnlyDictionary<long, string> map) =>
            value.GetValueKind() == JsonValueKind.Number && map.TryGetValue((long)value, out var n) ? n : $"#{value}";
        return key switch
        {
            "shipping_profile_id" => Name(ShippingProfiles),
            "return_policy_id" => Name(ReturnPolicies),
            "shop_section_id" => Name(Sections),
            "taxonomy_id" => Name(Categories),
            "production_partner_ids" => string.Join(", ", value.AsArray().Select(v => ProductionPartners.GetValueOrDefault((long)v!, $"#{v}"))) is { Length: > 0 } s ? s : "—",
            "tags" or "materials" => value.AsArray().Count == 0 ? "—" : string.Join(", ", value.AsArray().Select(v => (string?)v)),
            "who_made" or "when_made" or ListingFields.State => ListingFormat.Humanize((string?)value),
            ListingFields.Inventory => DescribeInventory(value),
            ListingFields.Images => string.Join(", ", value.AsArray().Select((p, i) =>
                $"{i + 1}. {((long?)p!["etsy_image_id"] is > 0 and long id ? $"#{id}" : "new photo")}" +
                ((string?)p["alt_text"] is { Length: > 0 } alt ? $" ({(alt.Length > 40 ? alt[..40] + "…" : alt)})" : ""))) is { Length: > 0 } photos ? photos : "No photos",
            "description" => (string?)value is { Length: > 160 } d ? d[..160] + "…" : (string?)value ?? "—",
            _ => value.GetValueKind() switch
            {
                JsonValueKind.True => "Yes",
                JsonValueKind.False => "No",
                JsonValueKind.Number => value.GetValue<decimal>().ToString("0.##", CultureInfo.CurrentCulture),
                _ => (string?)value ?? "—",
            },
        };
    }

    /// <summary>
    /// A short summary: each combination with its price for a few, otherwise the variations, the number of combinations
    /// and the price range (the preview lists the individual changes; see VariationEditor.Describe).
    /// </summary>
    private string DescribeInventory(JsonNode inventory)
    {
        var products = (inventory["products"]?.AsArray() ?? []).Select(p => p!).ToList();
        if (products.Count <= 6)
            return string.Join("; ", products.Select(p =>
            {
                var name = VariationName(p);
                var o = p["offerings"]?.AsArray().FirstOrDefault();
                if (o is null) return name;
                var price = ListingFormat.Price((long?)o["price_amount"] ?? 0, (int?)o["price_divisor"] ?? 100, CurrencyCode);
                var processing = (long?)o["readiness_state_id"] is { } r ? $", {ProcessingProfiles.GetValueOrDefault(r, $"#{r}")}" : "";
                var off = (bool?)o["is_enabled"] == false ? " (unavailable)" : "";
                return $"{name}: {price}{processing}{off}";
            }));

        var offerings = products.Select(p => p["offerings"]?.AsArray().FirstOrDefault()).OfType<JsonNode>().ToList();
        var prices = offerings.Select(o => ((long?)o["price_amount"] ?? 0, (int?)o["price_divisor"] ?? 100)).Distinct()
            .OrderBy(p => (decimal)p.Item1 / Math.Max(1, p.Item2)).ToList();
        var priceText = prices.Count switch
        {
            0 => "",
            1 => ListingFormat.Price(prices[0].Item1, prices[0].Item2, CurrencyCode),
            _ => $"{ListingFormat.Price(prices[0].Item1, prices[0].Item2, CurrencyCode)} to {ListingFormat.Price(prices[^1].Item1, prices[^1].Item2, CurrencyCode)}",
        };
        var processing = offerings.Select(o => (long?)o["readiness_state_id"]).OfType<long>().Distinct()
            .Select(r => ProcessingProfiles.GetValueOrDefault(r, $"#{r}")).ToList();
        var unavailable = offerings.Count(o => (bool?)o["is_enabled"] == false);
        var variations = VariationEditor.Variations(inventory).Select(v => $"{v.Name}: {string.Join(", ", v.Values)}");
        return $"{string.Join(" · ", variations)} · {products.Count} combinations, {priceText}" +
               (processing.Count > 0 ? $", {string.Join(" / ", processing)}" : "") +
               (unavailable > 0 ? $", {unavailable} unavailable" : "");
    }

    /// <summary>"Small / Black": the product's values, in Etsy's order of the variations when <paramref name="order"/> is given.</summary>
    public static string VariationName(JsonNode product, IReadOnlyList<long>? order = null) =>
        string.Join(" / ", (product["property_values"]?.AsArray() ?? [])
                .OrderBy(v => VariationEditor.Position(order, (long)v!["property_id"]!)).ThenBy(v => (long)v!["property_id"]!)
                .SelectMany(v => v!["values"]!.AsArray().Select(x => (string?)x)))
            is { Length: > 0 } n ? n : "Price";
}
