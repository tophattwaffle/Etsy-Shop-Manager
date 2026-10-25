using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>A value the listing offers for one of its variations.</summary>
public sealed record VariationOption(long? ValueId, string Value);

/// <summary>What the "Order changes" dialog edits: the order's address and each item's personalization and variations.</summary>
public sealed class OrderChangesForm
{
    public int OrderId { get; init; }
    public long ReceiptId { get; init; }

    /// <summary>The address on Etsy as editor text (name, then lines), shown beside the change and used to start one.</summary>
    public string EtsyAddress { get; init; } = "";

    public bool ChangeAddress { get; set; }
    public string Address { get; set; } = "";

    /// <summary>The seller's own note on the order (empty: none).</summary>
    public string SellerNote { get; set; } = "";

    /// <summary>Whether the seller note prints on the order's packing slip.</summary>
    public bool SellerNoteOnSlips { get; set; }

    public List<ItemChangesForm> Items { get; init; } = [];
}

public sealed class ItemChangesForm
{
    public int ItemId { get; init; }
    public string Title { get; init; } = "";
    public int Quantity { get; init; }

    /// <summary>The buyer's personalization (null when they gave none).</summary>
    public string? BuyersPersonalization { get; init; }

    public bool ChangePersonalization { get; set; }
    public string Personalization { get; set; } = "";
    public List<VariationChangeForm> Variations { get; init; } = [];
}

public sealed class VariationChangeForm
{
    public long PropertyId { get; init; }
    public string Name { get; init; } = "";
    public string BuyersValue { get; init; } = "";

    /// <summary>The value to make instead of the buyer's; null or the buyer's own value means no change.</summary>
    public string? Value { get; set; }

    /// <summary>The listing's values for this variation (empty when the listing isn't in the app).</summary>
    public List<VariationOption> Options { get; init; } = [];
}

/// <summary>
/// Saves the changes a buyer asked for after checkout (see <see cref="OrderOverrides"/>) on the local order. Never writes to
/// Etsy: the order on Etsy keeps what the buyer entered, and order polling leaves the changes alone.
/// </summary>
public sealed class OrderOverrideService(IDbContextFactory<AppDbContext> dbFactory, IAuditLog audit)
{
    /// <summary>The order's current changes, with each variation's choices from its listing; null if no such order.</summary>
    public async Task<OrderChangesForm?> GetFormAsync(int orderId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var order = await db.Orders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return null;

        var listingIds = order.Items.Select(i => i.EtsyListingId).OfType<long>().Distinct().ToList();
        var products = await db.ListingProducts.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Listing.ShopId == order.ShopId && listingIds.Contains(p.Listing.EtsyListingId))
            .OrderBy(p => p.Id)
            .Select(p => new { p.Listing.EtsyListingId, p.PropertyValues })
            .ToListAsync(ct);

        var etsyAddress = OrderOverrides.AddressText(OrderPrintService.EtsyAddress(order));
        return new OrderChangesForm
        {
            OrderId = order.Id,
            ReceiptId = order.EtsyReceiptId,
            EtsyAddress = etsyAddress,
            ChangeAddress = order.AddressOverride is not null,
            Address = order.AddressOverride ?? etsyAddress,
            SellerNote = order.SellerNote ?? "",
            SellerNoteOnSlips = order.SellerNoteOnSlips,
            Items =[.. order.Items.OrderBy(i => i.EtsyTransactionId).Select(i =>
            {
                var properties = products.Where(p => p.EtsyListingId == i.EtsyListingId).SelectMany(p => p.PropertyValues).ToList();
                var buyers = OrderOverrides.BuyersPersonalization(i);
                return new ItemChangesForm
                {
                    ItemId = i.Id,
                    Title = OrderPrintService.ShortTitle(i.Title),
                    Quantity = Math.Max(1, i.Quantity),
                    BuyersPersonalization = buyers,
                    ChangePersonalization = i.PersonalizationOverride is not null,
                    Personalization = i.PersonalizationOverride ?? buyers ?? "",
                    Variations = [.. i.Variations.Where(v => v.PropertyId != HaPayloadBuilder.PersonalizationPropertyId && v.Value.Length > 0)
                        .Select(v => new VariationChangeForm
                        {
                            PropertyId = v.PropertyId,
                            Name = v.Name,
                            BuyersValue = v.Value,
                            Value = OrderOverrides.VariationOverride(i, v)?.Value,
                            Options = Options(properties, v),
                        })],
                };
            })],
        };
    }

    /// <summary>
    /// The listing's values for the buyer's variation, matched by property (custom variations by name too, since Etsy
    /// numbers a listing's two custom variations 513 and 514 by position), in the listing's order. The buyer's own value
    /// is always among them.
    /// </summary>
    internal static List<VariationOption> Options(IEnumerable<PropertyValue> listingProperties, OrderVariation buyers)
    {
        var all = listingProperties.ToList();
        var matching = all.Where(p => p.PropertyId == buyers.PropertyId && SameName(p.PropertyName, buyers.Name)).ToList();
        if (matching.Count == 0) matching = [.. all.Where(p => SameName(p.PropertyName, buyers.Name))];
        if (matching.Count == 0) matching = [.. all.Where(p => p.PropertyId == buyers.PropertyId)];

        var options = new List<VariationOption>();
        foreach (var p in matching)
            for (var n = 0; n < p.Values.Count; n++)
                if (p.Values[n].Length > 0 && !options.Any(o => o.Value == p.Values[n]))
                    options.Add(new VariationOption(n < p.ValueIds.Count ? p.ValueIds[n] : null, p.Values[n]));
        if (options.Count > 0 && !options.Any(o => o.Value == buyers.Value)) options.Insert(0, new VariationOption(buyers.ValueId, buyers.Value));
        return options;
    }

    private static bool SameName(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves the form. Returns null when saved, otherwise what to fix (nothing is saved then).</summary>
    public async Task<string?> SaveAsync(OrderChangesForm form, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == form.OrderId, ct);
        if (order is null) return "This order is no longer in the app.";

        var changes = new List<string>();
        string? address = null;
        if (form.ChangeAddress)
        {
            var lines = OrderOverrides.Lines(form.Address);
            if (lines.Count < 2) return "Enter the name and the address, each on its own line.";
            address = string.Join("\n", lines);
            if (address.Length > OrderOverrides.MaxAddressLength) return $"Keep the address to {OrderOverrides.MaxAddressLength} characters.";
            // The same address as on Etsy isn't a change.
            if (address == string.Join("\n", OrderOverrides.Lines(form.EtsyAddress))) address = null;
        }
        if (address != order.AddressOverride) changes.Add(address is null ? "address back to Etsy's" : "address changed");
        order.AddressOverride = address;

        var note = string.IsNullOrWhiteSpace(form.SellerNote) ? null : form.SellerNote.Trim();
        if (note?.Length > OrderOverrides.MaxSellerNoteLength) return $"Keep the seller note to {OrderOverrides.MaxSellerNoteLength} characters.";
        if (note != order.SellerNote) changes.Add(note is null ? "seller note removed" : "seller note set");
        order.SellerNote = note;
        var onSlips = note is not null && form.SellerNoteOnSlips; // no note, nothing to print
        if (note is not null && onSlips != order.SellerNoteOnSlips)
            changes.Add(onSlips ? "seller note on packing slips" : "seller note off packing slips");
        order.SellerNoteOnSlips = onSlips;

        foreach (var f in form.Items)
        {
            var item = order.Items.FirstOrDefault(i => i.Id == f.ItemId);
            if (item is null) continue;
            var title = OrderPrintService.ShortTitle(item.Title);

            string? personalization = null;
            if (f.ChangePersonalization)
            {
                personalization = (f.Personalization ?? "").Trim();
                if (personalization.Length > OrderOverrides.MaxPersonalizationLength)
                    return $"Keep the personalization for {title} to {OrderOverrides.MaxPersonalizationLength} characters.";
                if (personalization == (OrderOverrides.BuyersPersonalization(item) ?? "")) personalization = null;
            }
            if (personalization != item.PersonalizationOverride)
                changes.Add(personalization is null ? $"{title}: buyer's personalization" : $"{title}: personalization changed");
            item.PersonalizationOverride = personalization;

            var overrides = new List<OrderVariation>();
            foreach (var v in f.Variations)
            {
                var buyers = item.Variations.FirstOrDefault(x => x.PropertyId == v.PropertyId && x.Name == v.Name);
                if (buyers is null || v.Value is null || v.Value == buyers.Value) continue;
                if (v.Options.FirstOrDefault(o => o.Value == v.Value) is not { } option)
                    return $"Choose {v.Name} for {title} from the listing's values.";
                overrides.Add(buyers with { ValueId = option.ValueId, Value = option.Value });
            }
            var before = string.Join(", ", item.VariationOverrides.Select(o => $"{o.Name}: {o.Value}"));
            var after = string.Join(", ", overrides.Select(o => $"{o.Name}: {o.Value}"));
            if (before != after) changes.Add(after.Length == 0 ? $"{title}: buyer's variations" : $"{title}: {after}");
            item.VariationOverrides = overrides;
        }

        if (changes.Count == 0) return null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("order.changes", nameof(Order), order.EtsyReceiptId.ToString(), string.Join("; ", changes), user, ct);
        return null;
    }
}
