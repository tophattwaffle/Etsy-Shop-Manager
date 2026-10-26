namespace EtsyShopManager.Core.Domain;

/// <summary>An Etsy receipt (one checkout), stored locally so order history survives and HA reads from here.</summary>
public class Order
{
    public int Id { get; set; }
    public int ShopId { get; set; }
    public Shop Shop { get; set; } = null!;

    public long EtsyReceiptId { get; set; }
    public long BuyerUserId { get; set; }
    public string? BuyerName { get; set; }

    /// <summary>Etsy's status: paid, completed, open, payment processing, canceled, fully/partially refunded.</summary>
    public string Status { get; set; } = "";
    public bool IsPaid { get; set; }
    public bool IsShipped { get; set; }
    public bool IsGift { get; set; }
    public string? GiftMessage { get; set; }

    /// <summary>
    /// The buyer's first name, printed under the gift message as Etsy's gift slip does ("- Kate"). From Etsy's buyer
    /// profile (getUser): receipts carry gift_sender, but Etsy leaves it empty.
    /// </summary>
    public string? GiftFromName { get; set; }

    /// <summary>
    /// The buyer's name on their Etsy profile ("Meg Taylor"), printed under Buyer as on Etsy's packing slip; the receipt
    /// only has the ship-to name. From getUser, for open and gift orders.
    /// </summary>
    public string? BuyerProfileName { get; set; }
    public string? MessageFromBuyer { get; set; }

    /// <summary>Money in minor units with the divisor Etsy sent (usually 100).</summary>
    public string? CurrencyCode { get; set; }
    public int Divisor { get; set; } = 100;
    public long? Subtotal { get; set; }
    public long? Grandtotal { get; set; }
    public long? ShippingCost { get; set; }
    public long? TaxCost { get; set; }
    public long? DiscountAmount { get; set; }

    public DateTime? EtsyCreatedAtUtc { get; set; }
    public DateTime? EtsyUpdatedAtUtc { get; set; }

    /// <summary>The earliest expected ship date of its items (the binding deadline).</summary>
    public DateTime? ShipByUtc { get; set; }

    /// <summary>The receipt as Etsy returned it (includes the address, for later packing slips).</summary>
    public string RawJson { get; set; } = "";

    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime LastSyncedAtUtc { get; set; }

    /// <summary>
    /// A shipping address the seller entered here when the buyer asked for a change (Etsy's can't be changed through the
    /// API): the name, then the address lines, one per line. Null when the order ships to the address on Etsy. Only this
    /// app knows it: polling never touches it, and the packing slip marks it so the shipping label gets changed too.
    /// </summary>
    public string? AddressOverride { get; set; }

    /// <summary>
    /// The seller's own note on the order, set in the Change order dialog and shown on the order card. Local only: never
    /// sent to Etsy, printed on slips only with <see cref="SellerNoteOnSlips"/>, and polling never touches it. Null when
    /// there's none.
    /// </summary>
    public string? SellerNote { get; set; }

    /// <summary>Whether <see cref="SellerNote"/> prints on the order's packing (or gift) slip.</summary>
    public bool SellerNoteOnSlips { get; set; }

    /// <summary>
    /// When the order's packing (or gift) slip was last printed: by the print station, or from the Orders page once the
    /// print dialog closed. Null when it never was. Local only; polling never touches it.
    /// </summary>
    public DateTime? SlipPrintedAtUtc { get; set; }

    /// <summary>
    /// When the slip was put in the print station's queue (automatic printing); null when it isn't waiting to print.
    /// Cleared once it prints.
    /// </summary>
    public DateTime? SlipQueuedAtUtc { get; set; }

    /// <summary>
    /// A note from the buyer the seller entered, edited or cleared here (empty when cleared). Null when the buyer's note
    /// stands. Only this app knows it: polling never touches it, and Etsy's API can't change an order.
    /// </summary>
    public string? MessageFromBuyerOverride { get; set; }

    public List<OrderItem> Items { get; set; } = [];

    /// <summary>Statuses that close an order even when it was never shipped.</summary>
    public static readonly string[] ResolvedStatuses = ["completed", "canceled", "cancelled", "fully refunded", "partially refunded"];

    /// <summary>Paid, not shipped, and not resolved: needs making/shipping.</summary>
    public bool IsOpen => IsPaid && !IsShipped && !ResolvedStatuses.Contains(Status.Trim().ToLowerInvariant());
}

/// <summary>An Etsy transaction: one line of a receipt.</summary>
public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public long EtsyTransactionId { get; set; }
    public long? EtsyListingId { get; set; }
    public long? EtsyProductId { get; set; }
    public long? ListingImageId { get; set; }
    public string? Title { get; set; }
    public string? Sku { get; set; }
    public int Quantity { get; set; }
    public long PriceAmount { get; set; }
    public int PriceDivisor { get; set; } = 100;
    public string? CurrencyCode { get; set; }

    /// <summary>Variations as Etsy formats them, personalization (property 54) included.</summary>
    public List<OrderVariation> Variations { get; set; } = [];

    /// <summary>
    /// Personalization the seller entered here when the buyer asked for a change, used in place of the buyer's everywhere
    /// the order is shown (empty: no personalization). Null when the buyer's stands. Polling never touches it.
    /// </summary>
    public string? PersonalizationOverride { get; set; }

    /// <summary>
    /// Variation values the seller chose here in place of the buyer's (one per property, from the listing's own values).
    /// Polling never touches them.
    /// </summary>
    public List<OrderVariation> VariationOverrides { get; set; } = [];

    public DateTime? ExpectedShipAtUtc { get; set; }
    public DateTime? EtsyCreatedAtUtc { get; set; }

    /// <summary>Etsy CDN URL of the item's photo, resolved when the order is stored.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// How many of <see cref="Quantity"/> the seller has marked as made (Orders page and To make). Only this app knows
    /// it: polling never touches it and nothing is sent to Etsy.
    /// </summary>
    public int MadeQuantity { get; set; }

    /// <summary>The units to make (Etsy's quantity, at least one), as the To make list counts them.</summary>
    public int UnitsToMake => Math.Max(1, Quantity);

    /// <summary>Units marked made, never more than <see cref="UnitsToMake"/> (Etsy's quantity can drop after marking).</summary>
    public int UnitsMade => Math.Clamp(MadeQuantity, 0, UnitsToMake);
}

public sealed record OrderVariation
{
    public long PropertyId { get; init; }
    public long? ValueId { get; init; }
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
}

/// <summary>Order polling progress per shop.</summary>
public class ShopOrderSyncState
{
    public int ShopId { get; set; }
    public Shop Shop { get; set; } = null!;

    /// <summary>Receipts modified at or after this are requested on the next poll (with a small overlap).</summary>
    public DateTime? ModifiedSinceUtc { get; set; }

    /// <summary>Offset within the current poll, so an interrupted backfill resumes where it stopped.</summary>
    public int Offset { get; set; }

    /// <summary>When the current (or last) poll started; the next poll asks for changes since then.</summary>
    public DateTime? PollStartedAtUtc { get; set; }

    /// <summary>Set after the first complete pull; new-order events only fire after that.</summary>
    public DateTime? BackfillCompletedAtUtc { get; set; }

    public DateTime? LastPolledAtUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAtUtc { get; set; }
}

/// <summary>An API key for machine clients (Home Assistant). Only a hash is stored.</summary>
public class ApiKey
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>The first characters of the key, shown in the UI to tell keys apart.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>SHA-256 (hex) of the full key.</summary>
    public string Hash { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}
