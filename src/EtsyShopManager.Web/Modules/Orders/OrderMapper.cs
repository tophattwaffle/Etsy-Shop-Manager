using EtsyShopManager.Core.Domain;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Modules.Listings;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>Copies an Etsy receipt onto the local order. Pure, so it is unit tested directly.</summary>
public static class OrderMapper
{
    public static void Apply(Order target, EtsyReceipt source, string rawJson, DateTime nowUtc)
    {
        target.EtsyReceiptId = source.ReceiptId;
        target.BuyerUserId = source.BuyerUserId;
        target.BuyerName = string.IsNullOrWhiteSpace(source.Name) ? null : Text(source.Name);
        target.Status = source.Status ?? "";
        target.IsPaid = source.IsPaid;
        target.IsShipped = source.IsShipped;
        target.IsGift = source.IsGift;
        target.GiftMessage = string.IsNullOrEmpty(source.GiftMessage) ? null : Text(source.GiftMessage);
        target.MessageFromBuyer = string.IsNullOrEmpty(source.MessageFromBuyer) ? null : Text(source.MessageFromBuyer);

        var money = source.Grandtotal ?? source.Subtotal;
        target.CurrencyCode = money?.CurrencyCode;
        target.Divisor = money?.Divisor is > 0 ? money.Divisor : 100;
        target.Subtotal = Minor(source.Subtotal, target.Divisor);
        target.Grandtotal = Minor(source.Grandtotal, target.Divisor);
        target.ShippingCost = Minor(source.TotalShippingCost, target.Divisor);
        target.TaxCost = Minor(source.TotalTaxCost, target.Divisor);
        target.DiscountAmount = Minor(source.DiscountAmt, target.Divisor);

        target.EtsyCreatedAtUtc = ListingMapper.FromUnix(source.CreatedTimestamp);
        target.EtsyUpdatedAtUtc = ListingMapper.FromUnix(source.UpdatedTimestamp);
        target.RawJson = rawJson;
        target.LastSyncedAtUtc = nowUtc;

        SyncItems(target, source.Transactions ?? []);
        target.ShipByUtc = target.Items.Select(i => i.ExpectedShipAtUtc).Where(d => d is not null).Min();
    }

    /// <summary>
    /// Etsy sends a receipt's text HTML-encoded (an apostrophe arrives as <c>&amp;#39;</c>), like listing text: decoded once
    /// here, so the app stores and shows plain text and every page encodes it the usual way.
    /// </summary>
    public static string Text(string? html) => System.Net.WebUtility.HtmlDecode(html ?? "");

    /// <summary>Re-expresses an amount in the order's divisor (Etsy uses the same one throughout a receipt).</summary>
    private static long? Minor(EtsyMoney? money, int divisor) =>
        money is null ? null
        : money.Divisor == divisor || money.Divisor <= 0 ? money.Amount
        : (long)Math.Round((decimal)money.Amount * divisor / money.Divisor);

    private static void SyncItems(Order target, List<EtsyReceiptTransaction> transactions)
    {
        var ids = transactions.Select(t => t.TransactionId).ToHashSet();
        target.Items.RemoveAll(i => !ids.Contains(i.EtsyTransactionId));

        foreach (var t in transactions.GroupBy(t => t.TransactionId).Select(g => g.First()))
        {
            var item = target.Items.FirstOrDefault(i => i.EtsyTransactionId == t.TransactionId);
            if (item is null)
            {
                item = new OrderItem { EtsyTransactionId = t.TransactionId };
                target.Items.Add(item);
            }

            // A different photo or listing means the stored image URL no longer applies.
            if (item.EtsyListingId != t.ListingId || item.ListingImageId != t.ListingImageId) item.ImageUrl = null;

            item.EtsyListingId = t.ListingId;
            item.EtsyProductId = t.ProductId;
            item.ListingImageId = t.ListingImageId;
            item.Title = t.Title is null ? null : Text(t.Title);
            item.Sku = string.IsNullOrEmpty(t.Sku) ? null : t.Sku;
            item.Quantity = t.Quantity;
            item.PriceAmount = t.Price?.Amount ?? 0;
            item.PriceDivisor = t.Price?.Divisor is > 0 ? t.Price.Divisor : 100;
            item.CurrencyCode = t.Price?.CurrencyCode;
            item.ExpectedShipAtUtc = ListingMapper.FromUnix(t.ExpectedShipDate);
            item.EtsyCreatedAtUtc = ListingMapper.FromUnix(t.CreatedTimestamp);

            var variations = (t.Variations ?? []).Select(v => new OrderVariation
            {
                PropertyId = v.PropertyId,
                ValueId = v.ValueId,
                Name = Text(v.FormattedName),
                Value = Text(v.FormattedValue),
            }).ToList();
            if (!item.Variations.SequenceEqual(variations)) item.Variations = variations;
        }
    }

    /// <summary>Picks a dashboard-sized image URL from an Etsy listing image (smallest first, like the old integration).</summary>
    public static string? ThumbnailUrl(EtsyListingImage image) =>
        image.Url170x135 ?? image.Url570xN ?? image.UrlFullxFull;
}
