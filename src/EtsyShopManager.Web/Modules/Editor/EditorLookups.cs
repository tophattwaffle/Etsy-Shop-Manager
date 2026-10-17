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

    private string DescribeInventory(JsonNode inventory) =>
        string.Join("; ", (inventory["products"]?.AsArray() ?? []).Select(p =>
        {
            var name = VariationName(p!);
            var o = p!["offerings"]?.AsArray().FirstOrDefault();
            if (o is null) return name;
            var price = ListingFormat.Price((long?)o["price_amount"] ?? 0, (int?)o["price_divisor"] ?? 100, CurrencyCode);
            var processing = (long?)o["readiness_state_id"] is { } r ? $", {ProcessingProfiles.GetValueOrDefault(r, $"#{r}")}" : "";
            return $"{name}: {price}{processing}";
        }));

    public static string VariationName(JsonNode product) =>
        string.Join(" / ", (product["property_values"]?.AsArray() ?? []).SelectMany(v => v!["values"]!.AsArray().Select(x => (string?)x)))
            is { Length: > 0 } n ? n : "Price";
}
