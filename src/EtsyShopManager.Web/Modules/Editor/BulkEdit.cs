using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

public enum BulkEditKind { Price, AddTags, RemoveTags, AddMaterials, RemoveMaterials, ShippingProfile, Section }

public enum PriceRounding
{
    None,
    /// <summary>To the nearest price ending in .99 (21.37 → 20.99, 21.60 → 21.99).</summary>
    To99,
    /// <summary>To the nearest whole amount.</summary>
    Whole,
}

/// <summary>One bulk change to many listings.</summary>
/// <param name="Percent">Price: change by this percent (10 = 10% more, -10 = 10% less).</param>
/// <param name="Amount">Price: change by this amount in the listing's currency (used when <paramref name="Percent"/> is null).</param>
/// <param name="Rounding">Price: rounding after a percent change (an amount change is exact, so it isn't rounded).</param>
/// <param name="Words">Tags or materials to add or remove.</param>
/// <param name="PerShop">Shipping profile or section per shop (app shop id → Etsy id; a null section means none). Shops left out are skipped.</param>
public sealed record BulkEdit(BulkEditKind Kind, decimal? Percent = null, decimal? Amount = null, PriceRounding Rounding = PriceRounding.None,
    IReadOnlyList<string>? Words = null, IReadOnlyDictionary<int, long?>? PerShop = null)
{
    public string FieldKey => Kind switch
    {
        BulkEditKind.Price => ListingFields.Inventory,
        BulkEditKind.AddTags or BulkEditKind.RemoveTags => "tags",
        BulkEditKind.AddMaterials or BulkEditKind.RemoveMaterials => "materials",
        BulkEditKind.ShippingProfile => "shipping_profile_id",
        _ => "shop_section_id",
    };

    /// <summary>The rounding that applies: only a percent change is rounded.</summary>
    public PriceRounding AppliedRounding => Percent is null ? PriceRounding.None : Rounding;

    /// <summary>Why the change can't be previewed as given, in words (null when it can).</summary>
    public string? Problem => Kind switch
    {
        BulkEditKind.Price when Percent is null && Amount is null => "Enter a percent or an amount.",
        BulkEditKind.Price when Percent is <= -100 => "A price can't go down by 100% or more.",
        BulkEditKind.Price when (Percent ?? Amount) == 0 && AppliedRounding == PriceRounding.None => "Enter a change other than zero, or choose rounding.",
        BulkEditKind.AddTags or BulkEditKind.RemoveTags or BulkEditKind.AddMaterials or BulkEditKind.RemoveMaterials
            when !(Words ?? []).Any(w => w.Trim().Length > 0) => $"Enter the {(FieldKey == "tags" ? "tags" : "materials")}, separated by commas.",
        BulkEditKind.ShippingProfile or BulkEditKind.Section when PerShop is not { Count: > 0 } => "Choose a value for at least one shop.",
        _ => null,
    };

    /// <summary>The change in words, for the audit log ("Price +10%, rounded to .99").</summary>
    public string Describe() => Kind switch
    {
        BulkEditKind.Price => "Price " + (Percent is { } p
            ? p.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + "%"
            : (Amount ?? 0).ToString("+0.00;-0.00;0", CultureInfo.InvariantCulture)) + AppliedRounding switch
        {
            PriceRounding.To99 => ", rounded to .99",
            PriceRounding.Whole => ", rounded to whole amounts",
            _ => "",
        },
        BulkEditKind.AddTags => $"Add tags: {string.Join(", ", Words ?? [])}",
        BulkEditKind.RemoveTags => $"Remove tags: {string.Join(", ", Words ?? [])}",
        BulkEditKind.AddMaterials => $"Add materials: {string.Join(", ", Words ?? [])}",
        BulkEditKind.RemoveMaterials => $"Remove materials: {string.Join(", ", Words ?? [])}",
        BulkEditKind.ShippingProfile => "Shipping profile",
        _ => "Shop section",
    };
}

/// <summary>What a bulk change does to one listing: before and after in words, or why it's skipped.</summary>
public sealed record BulkEditRow(int ListingId, string Title, string ShopName, string Before, string After, string? Skipped)
{
    public bool Changes => Skipped is null && Before != After;
}

/// <summary>A shop among the selected listings, with what its shipping profile and section can be set to.</summary>
public sealed record BulkEditShop(int ShopId, string ShopName, int Listings, IReadOnlyDictionary<long, string> ShippingProfiles,
    IReadOnlyDictionary<long, string> Sections);

/// <summary>
/// Bulk edits from the listings page: price by percent or amount (every variation's price, with optional rounding), tags and
/// materials added or removed, shipping profile or shop section per shop. A preview shows each listing's before and after
/// (or why it's skipped: changed on Etsy, over Etsy's limits); applying saves unpushed edits only, and the listings then go
/// through each one's normal reviewed push.
/// </summary>
public sealed class BulkListingEditor(IDbContextFactory<AppDbContext> dbFactory, ListingEditor editor, IAuditLog audit)
{
    /// <summary>Etsy's lowest price for a listing (USD 0.20).</summary>
    public const decimal MinPrice = 0.20m;

    /// <summary>The shops the listings belong to, with their shipping profiles and sections.</summary>
    public async Task<IReadOnlyList<BulkEditShop>> ShopsAsync(IReadOnlyCollection<int> listingIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = listingIds.ToList();
        var shops = await db.Listings.AsNoTracking().Where(l => ids.Contains(l.Id))
            .GroupBy(l => new { l.ShopId, l.Shop.Name }).Select(g => new { g.Key.ShopId, g.Key.Name, Count = g.Count() }).ToListAsync(ct);
        var result = new List<BulkEditShop>();
        foreach (var s in shops.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var names = await EditorLookups.LoadAsync(db, s.ShopId, [], null, ct);
            result.Add(new BulkEditShop(s.ShopId, s.Name, s.Count, names.ShippingProfiles, names.Sections));
        }
        return result;
    }

    public async Task<IReadOnlyList<BulkEditRow>> PreviewAsync(IReadOnlyCollection<int> listingIds, BulkEdit edit, CancellationToken ct = default) =>
        [.. (await PlanAsync(listingIds, edit, ct)).Select(p => p.Row)];

    /// <summary>
    /// Saves the changes as unpushed edits (nothing is sent to Etsy). The plan is worked out again here, so a listing
    /// changed on Etsy since the preview is skipped. Returns the listings changed.
    /// </summary>
    public async Task<IReadOnlyList<int>> ApplyAsync(IReadOnlyCollection<int> listingIds, BulkEdit edit, ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        RequireEditor(user);
        if (edit.Problem is { } problem) throw new ArgumentException(problem);
        var name = user.Identity?.Name;
        var changed = new List<int>();
        foreach (var (row, value) in await PlanAsync(listingIds, edit, ct))
        {
            if (!row.Changes) continue;
            await editor.SaveAsync(row.ListingId, new Dictionary<string, JsonNode?> { [edit.FieldKey] = value }, name, ct);
            changed.Add(row.ListingId);
        }
        if (changed.Count > 0)
            await audit.WriteAsync("listing.bulk-edited", nameof(Listing), null,
                $"{changed.Count} listing(s): {edit.Describe()} (unpushed edits)", name, ct);
        return changed;
    }

    /// <summary>The review before pushing: each of the listings with unpushed edits, with the editor's own preview.</summary>
    public async Task<IReadOnlyList<TemplatePushItem>> ReviewAsync(IReadOnlyCollection<int> listingIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = listingIds.ToList();
        var listings = await db.Listings.AsNoTracking().Where(l => ids.Contains(l.Id) && l.EtsyState != null)
            .Select(l => new { l.Id, l.Title, Shop = l.Shop.Name }).ToListAsync(ct);
        var items = new List<TemplatePushItem>();
        foreach (var l in listings.OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase))
        {
            var preview = await editor.PreviewAsync(l.Id, ct);
            if (preview.Changes.Count > 0) items.Add(new TemplatePushItem(l.Id, l.Title, l.Shop, preview));
        }
        return items;
    }

    /// <summary>Queues the normal push for every reviewed listing that can be pushed. Returns the outcome per listing.</summary>
    public async Task<IReadOnlyList<(int ListingId, string Title, int? PushId, IReadOnlyList<string> Blockers)>> PushAsync(
        IReadOnlyCollection<int> listingIds, ClaimsPrincipal user, CancellationToken ct = default)
    {
        RequireEditor(user);
        var results = new List<(int, string, int?, IReadOnlyList<string>)>();
        foreach (var item in await ReviewAsync(listingIds, ct))
        {
            var (pushId, blockers) = await editor.RequestPushAsync(item.ListingId, user.Identity?.Name, ct);
            results.Add((item.ListingId, item.Title, pushId, blockers));
        }
        return results;
    }

    private static void RequireEditor(ClaimsPrincipal user)
    {
        if (!user.IsInRole(Roles.Admin) && !user.IsInRole(Roles.Editor))
            throw new UnauthorizedAccessException("Only admins and editors can bulk edit listings.");
    }

    private async Task<List<(BulkEditRow Row, JsonNode? Value)>> PlanAsync(IReadOnlyCollection<int> listingIds, BulkEdit edit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = listingIds.ToList();
        var listings = await db.Listings.AsNoTracking().Include(l => l.EtsyState).Include(l => l.Shop)
            .Where(l => ids.Contains(l.Id)).ToListAsync(ct);
        var lookups = new Dictionary<int, EditorLookups>();
        var result = new List<(BulkEditRow, JsonNode?)>();
        foreach (var l in listings.OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (!lookups.TryGetValue(l.ShopId, out var names))
                lookups[l.ShopId] = names = await EditorLookups.LoadAsync(db, l.ShopId, [], l.CurrencyCode, ct);
            BulkEditRow Row(string before, string after, string? skipped = null) => new(l.Id, l.Title, l.Shop.Name, before, after, skipped);
            if (l.EtsyState is null) { result.Add((Row("", "", "Not imported yet."), null)); continue; }
            var model = ListingEditModel.Parse(l.EtsyState.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);
            var current = model.Local(edit.FieldKey);
            var before = Describe(edit, names, current);
            if (l.IsMissingOnEtsy) { result.Add((Row(before, before, "Gone from Etsy."), null)); continue; }
            if (model.DriftFields.Contains(edit.FieldKey))
            {
                result.Add((Row(before, before, $"{ListingEditor.Label(edit.FieldKey)} changed on Etsy; resolve it in the editor first."), null));
                continue;
            }
            var (value, problem) = Change(edit, l.ShopId, names, current);
            result.Add(problem is not null ? (Row(before, before, problem), null) : (Row(before, Describe(edit, names, value)), value));
        }
        return result;
    }

    /// <summary>The new value of the field, or why it can't change (pure).</summary>
    public static (JsonNode? Value, string? Problem) Change(BulkEdit edit, int shopId, EditorLookups names, JsonNode? current)
    {
        switch (edit.Kind)
        {
            case BulkEditKind.Price:
                if (current?["products"] is not JsonArray { Count: > 0 }) return (null, "No prices imported.");
                var inventory = current.DeepClone();
                foreach (var offering in (inventory["products"]?.AsArray() ?? []).SelectMany(p => p!["offerings"]?.AsArray() ?? []).Select(o => o!))
                {
                    var divisor = (int?)offering["price_divisor"] is int d && d > 0 ? d : 100;
                    var price = NewPrice((decimal)((long?)offering["price_amount"] ?? 0) / divisor, edit);
                    if (price < MinPrice) return (null, $"A price would be {Money(price)}; Etsy's lowest is {Money(MinPrice)}.");
                    offering["price_amount"] = (long)Math.Round(price * divisor);
                }
                return (inventory, null);
            case BulkEditKind.AddTags or BulkEditKind.AddMaterials or BulkEditKind.RemoveTags or BulkEditKind.RemoveMaterials:
                var tags = edit.Kind is BulkEditKind.AddTags or BulkEditKind.RemoveTags;
                var words = (current?.AsArray() ?? []).Select(v => (string?)v ?? "").ToList();
                var given = (edit.Words ?? []).Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (edit.Kind is BulkEditKind.RemoveTags or BulkEditKind.RemoveMaterials)
                    words.RemoveAll(w => given.Contains(w, StringComparer.OrdinalIgnoreCase));
                else
                    words.AddRange(given.Where(g => !words.Contains(g, StringComparer.OrdinalIgnoreCase)));
                var max = tags ? ListingEditor.MaxTags : ListingEditor.MaxMaterials;
                var what = tags ? "tags" : "materials";
                if (words.Count > max) return (null, $"Would have {words.Count} {what}; Etsy allows {max}.");
                if (tags && words.FirstOrDefault(w => w.Length > ListingEditor.MaxTagLength) is { } longTag)
                    return (null, $"The tag \"{longTag}\" is longer than {ListingEditor.MaxTagLength} characters.");
                if ((tags ? ListingTextRules.TagProblems(words) : ListingTextRules.MaterialProblems(words)).FirstOrDefault() is { } rule)
                    return (null, rule);
                return (new JsonArray([.. words.Select(w => (JsonNode)w)]), null);
            default:
                if (edit.PerShop is null || !edit.PerShop.TryGetValue(shopId, out var id))
                    return (null, "Nothing chosen for this shop.");
                var map = edit.Kind == BulkEditKind.ShippingProfile ? names.ShippingProfiles : names.Sections;
                if (id is null && edit.Kind == BulkEditKind.ShippingProfile) return (null, "Choose a shipping profile.");
                if (id is { } chosen && !map.ContainsKey(chosen)) return (null, "That choice isn't one of this shop's.");
                return (id is null ? null : JsonValue.Create(id.Value), null);
        }
    }

    /// <summary>A price after the change and rounding (pure).</summary>
    public static decimal NewPrice(decimal price, BulkEdit edit)
    {
        var changed = edit.Percent is { } percent ? price * (1 + percent / 100m) : price + (edit.Amount ?? 0);
        return edit.AppliedRounding switch
        {
            PriceRounding.To99 => Math.Max(0.99m, Math.Round(changed, MidpointRounding.AwayFromZero) - 0.01m),
            PriceRounding.Whole => Math.Round(changed, MidpointRounding.AwayFromZero),
            _ => Math.Round(changed, 2, MidpointRounding.AwayFromZero),
        };
    }

    private static string Describe(BulkEdit edit, EditorLookups names, JsonNode? value)
    {
        if (edit.Kind != BulkEditKind.Price) return value is null && edit.Kind == BulkEditKind.Section ? "No section" : names.Describe(edit.FieldKey, value);
        var prices = (value?["products"]?.AsArray() ?? []).SelectMany(p => p!["offerings"]?.AsArray() ?? [])
            .Select(o => (Amount: (long?)o!["price_amount"] ?? 0, Divisor: (int?)o["price_divisor"] is int d && d > 0 ? d : 100))
            .DistinctBy(p => (decimal)p.Amount / p.Divisor).OrderBy(p => (decimal)p.Amount / p.Divisor).ToList();
        return prices.Count switch
        {
            0 => "—",
            1 => ListingFormat.Price(prices[0].Amount, prices[0].Divisor, names.CurrencyCode),
            _ => $"{ListingFormat.Price(prices[0].Amount, prices[0].Divisor, names.CurrencyCode)} to " +
                 ListingFormat.Price(prices[^1].Amount, prices[^1].Divisor, names.CurrencyCode),
        };
    }

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
