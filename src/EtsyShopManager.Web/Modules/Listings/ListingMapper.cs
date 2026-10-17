using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Etsy.Models;

namespace EtsyShopManager.Web.Modules.Listings;

/// <summary>Copies Etsy listing data onto the local entities. Pure: no I/O, so it is unit tested directly.</summary>
public static class ListingMapper
{
    public static DateTime? FromUnix(long? seconds) =>
        seconds is > 0 ? DateTime.UnixEpoch.AddSeconds(seconds.Value) : null;

    /// <summary>Etsy's "last modified" time for a listing (what tells us its details may have changed).</summary>
    public static DateTime? LastModified(EtsyListing l) => FromUnix(l.LastModifiedTimestamp ?? l.UpdatedTimestamp);

    public static void ApplyFields(Listing target, EtsyListing source)
    {
        target.EtsyListingId = source.ListingId;
        target.State = source.State;
        // Etsy returns text HTML-encoded ("you&#39;re", "&quot;"). Keep plain text, so it shows and is pushed as written
        // (Etsy encodes it again in its own output).
        target.Title = Text(source.Title);
        target.Description = Text(source.Description);
        target.PriceAmount = source.Price?.Amount ?? 0;
        target.PriceDivisor = source.Price?.Divisor is > 0 ? source.Price.Divisor : 100;
        target.CurrencyCode = source.Price?.CurrencyCode;
        target.Quantity = source.Quantity;
        target.Skus = source.Skus?.Where(s => !string.IsNullOrEmpty(s)).ToList() ?? [];
        target.Tags = source.Tags?.Select(Text).ToList() ?? [];
        target.Materials = source.Materials?.Select(Text).ToList() ?? [];
        target.Styles = source.Style?.Select(Text).ToList() ?? [];
        target.TaxonomyId = source.TaxonomyId;
        target.ShippingProfileId = source.ShippingProfileId;
        target.ReturnPolicyId = source.ReturnPolicyId;
        target.ReadinessStateId = source.ReadinessStateId;
        target.ShopSectionId = source.ShopSectionId;
        target.FeaturedRank = source.FeaturedRank is >= 0 ? source.FeaturedRank : null;
        target.WhoMade = source.WhoMade;
        target.WhenMade = source.WhenMade;
        target.IsSupply = source.IsSupply;
        target.ListingType = source.ListingType;
        target.IsCustomizable = source.IsCustomizable;
        target.IsPersonalizable = source.IsPersonalizable;
        target.IsTaxable = source.IsTaxable;
        target.ShouldAutoRenew = source.ShouldAutoRenew;
        target.HasVariations = source.HasVariations;
        target.ProcessingMin = source.ProcessingMin;
        target.ProcessingMax = source.ProcessingMax;
        target.ItemWeight = source.ItemWeight;
        target.ItemWeightUnit = source.ItemWeightUnit;
        target.ItemLength = source.ItemLength;
        target.ItemWidth = source.ItemWidth;
        target.ItemHeight = source.ItemHeight;
        target.ItemDimensionsUnit = source.ItemDimensionsUnit;
        target.ProductionPartnerIds = source.ProductionPartners?.Select(p => p.ProductionPartnerId).ToList() ?? [];
        target.PersonalizationJson = PersonalizationJson(source);
        target.Url = source.Url;
        target.Views = source.Views;
        target.NumFavorers = source.NumFavorers;
        target.EtsyCreatedAtUtc = FromUnix(source.CreatedTimestamp ?? source.OriginalCreationTimestamp);
        target.EtsyUpdatedAtUtc = LastModified(source);
        target.EtsyStateChangedAtUtc = FromUnix(source.StateTimestamp);
        target.EtsyEndsAtUtc = FromUnix(source.EndingTimestamp);
    }

    /// <summary>Etsy's HTML-encoded text as plain text.</summary>
    public static string Text(string? html) => System.Net.WebUtility.HtmlDecode(html ?? "");

    /// <summary>Personalization questions as JSON, or null when the listing has none.</summary>
    private static string? PersonalizationJson(EtsyListing source)
    {
        if (source.Personalization is not { } p || p.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!p.TryGetProperty("personalization_questions", out var q) || q.ValueKind != System.Text.Json.JsonValueKind.Array
            || q.GetArrayLength() == 0)
        {
            return null;
        }
        return q.GetRawText();
    }

    public static PropertyValue ToPropertyValue(EtsyPropertyValue v) => new()
    {
        PropertyId = v.PropertyId,
        PropertyName = v.PropertyName,
        ScaleId = v.ScaleId,
        ScaleName = v.ScaleName,
        ValueIds = v.ValueIds ?? [],
        Values = v.Values ?? [],
    };

    /// <summary>
    /// Brings the local images in line with Etsy's, matched by listing_image_id. A changed source URL clears the
    /// stored files so the image job downloads the new content.
    /// </summary>
    public static void SyncImages(Listing target, IReadOnlyList<EtsyListingImage> source)
    {
        var incoming = source.Where(i => !string.IsNullOrEmpty(i.UrlFullxFull))
            .GroupBy(i => i.ListingImageId).Select(g => g.First()).ToList();
        var ids = incoming.Select(i => i.ListingImageId).ToHashSet();
        target.Images.RemoveAll(i => !ids.Contains(i.EtsyImageId));

        foreach (var src in incoming)
        {
            var img = target.Images.FirstOrDefault(i => i.EtsyImageId == src.ListingImageId);
            if (img is null)
            {
                img = new ListingImage { EtsyImageId = src.ListingImageId };
                target.Images.Add(img);
            }

            var thumbUrl = src.Url570xN ?? src.Url170x135 ?? src.Url75x75;
            if (img.SourceUrl != src.UrlFullxFull)
            {
                img.SourceUrl = src.UrlFullxFull!;
                img.FileName = null;
                img.ByteSize = null;
                img.DownloadedAtUtc = null;
                img.DownloadError = null;
            }
            if (img.SourceThumbUrl != thumbUrl)
            {
                img.SourceThumbUrl = thumbUrl;
                img.ThumbFileName = null;
            }

            img.Rank = src.Rank;
            img.AltText = src.AltText is null ? null : Text(src.AltText);
            img.FullWidth = src.FullWidth;
            img.FullHeight = src.FullHeight;
            img.HexCode = src.HexCode;
        }
    }

    /// <summary>
    /// Brings the local inventory in line with Etsy's, matching products and offerings by Etsy id (Etsy issues new
    /// ids whenever the inventory is written, so a write replaces all rows; a plain re-import changes nothing).
    /// </summary>
    /// <summary>
    /// The listing-level stock Etsy reports for an inventory: with one shared quantity (it doesn't vary by a property)
    /// every variation holds the same number and Etsy reports that number; otherwise the sum over enabled variations.
    /// Used after our own inventory writes, which return the inventory but not the listing.
    /// </summary>
    public static int ListingQuantity(EtsyInventory source)
    {
        var enabled = source.Products.Where(p => !p.IsDeleted)
            .SelectMany(p => p.Offerings.Where(o => !o.IsDeleted && o.IsEnabled))
            .Select(o => o.Quantity).ToList();
        if (enabled.Count == 0) return 0;
        return (source.QuantityOnProperty ?? []).Count == 0 ? enabled.Max() : enabled.Sum();
    }

    public static void SyncInventory(Listing target, EtsyInventory source)
    {
        target.PriceOnProperty = source.PriceOnProperty ?? [];
        target.QuantityOnProperty = source.QuantityOnProperty ?? [];
        target.SkuOnProperty = source.SkuOnProperty ?? [];
        target.ReadinessStateOnProperty = source.ReadinessStateOnProperty ?? [];

        var productIds = source.Products.Select(p => p.ProductId).ToHashSet();
        target.Products.RemoveAll(p => !productIds.Contains(p.EtsyProductId));

        foreach (var src in source.Products.GroupBy(p => p.ProductId).Select(g => g.First()))
        {
            var product = target.Products.FirstOrDefault(p => p.EtsyProductId == src.ProductId);
            if (product is null)
            {
                product = new ListingProduct { EtsyProductId = src.ProductId };
                target.Products.Add(product);
            }

            product.Sku = string.IsNullOrEmpty(src.Sku) ? null : src.Sku;
            product.IsDeleted = src.IsDeleted;
            var values = src.PropertyValues.Select(ToPropertyValue).ToList();
            if (!SamePropertyValues(product.PropertyValues, values)) product.PropertyValues = values;

            var offeringIds = src.Offerings.Select(o => o.OfferingId).ToHashSet();
            product.Offerings.RemoveAll(o => !offeringIds.Contains(o.EtsyOfferingId));
            foreach (var so in src.Offerings.GroupBy(o => o.OfferingId).Select(g => g.First()))
            {
                var offering = product.Offerings.FirstOrDefault(o => o.EtsyOfferingId == so.OfferingId);
                if (offering is null)
                {
                    offering = new ListingOffering { EtsyOfferingId = so.OfferingId };
                    product.Offerings.Add(offering);
                }
                offering.PriceAmount = so.Price?.Amount ?? 0;
                offering.PriceDivisor = so.Price?.Divisor is > 0 ? so.Price.Divisor : 100;
                offering.CurrencyCode = so.Price?.CurrencyCode;
                offering.Quantity = so.Quantity;
                offering.IsEnabled = so.IsEnabled;
                offering.IsDeleted = so.IsDeleted;
                offering.ReadinessStateId = so.ReadinessStateId;
            }
        }
    }

    public static void SetAttributes(Listing target, IEnumerable<EtsyPropertyValue> attributes)
    {
        var values = attributes.Select(ToPropertyValue).ToList();
        if (!SamePropertyValues(target.Attributes, values)) target.Attributes = values;
    }

    private static bool SamePropertyValues(List<PropertyValue> a, List<PropertyValue> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            p.First.PropertyId == p.Second.PropertyId && p.First.PropertyName == p.Second.PropertyName &&
            p.First.ScaleId == p.Second.ScaleId && p.First.ScaleName == p.Second.ScaleName &&
            p.First.ValueIds.SequenceEqual(p.Second.ValueIds) && p.First.Values.SequenceEqual(p.Second.Values));

    /// <summary>Records the listing as Etsy holds it now: raw JSON plus the canonical managed fields and their hash.</summary>
    public static void SetEtsyState(Listing target, string rawJson, DateTime nowUtc)
    {
        var managedJson = ManagedListingState.From(target).ToCanonicalJson();
        var hash = ManagedListingState.Hash(managedJson);

        target.EtsyState ??= new ListingEtsyState { ManagedChangedAtUtc = nowUtc };
        var state = target.EtsyState;
        if (state.ManagedHash != hash || state.ManagedVersion != ManagedListingState.CurrentVersion)
        {
            if (state.ManagedHash != "") state.ManagedChangedAtUtc = nowUtc;
            // A new managed-field definition makes the old base incomparable: start again from Etsy's state.
            if (state.ManagedVersion != ManagedListingState.CurrentVersion) state.BaseJson = null;
            state.ManagedHash = hash;
            state.ManagedJson = managedJson;
            state.ManagedVersion = ManagedListingState.CurrentVersion;
        }
        if (state.RawJson != rawJson) state.RawJson = rawJson;
        state.FetchedAtUtc = nowUtc;
    }

    /// <summary>
    /// Brings the editor bookkeeping in line with the Etsy state just stored: updates the base (untracked fields follow
    /// Etsy), drops draft entries that no longer differ, and sets HasDraft/HasDrift. Returns the drifted fields.
    /// </summary>
    public static IReadOnlyList<string> UpdateEditState(Listing target)
    {
        var state = target.EtsyState ?? throw new InvalidOperationException("SetEtsyState first.");
        var model = ListingEditModel.Parse(state.ManagedJson, state.BaseJson, target.DraftJson);
        Apply(target, model);
        return model.DriftFields;
    }

    /// <summary>Stores a (changed) edit model back on the listing.</summary>
    public static void Apply(Listing target, ListingEditModel model)
    {
        var baseJson = model.Base.ToJsonString();
        if (target.EtsyState!.BaseJson != baseJson) target.EtsyState.BaseJson = baseJson;
        target.DraftJson = model.HasDraft ? model.Draft.ToJsonString() : null;
        target.HasDraft = model.HasDraft;
        target.HasDrift = model.DriftFields.Count > 0;
    }
}
