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

    public DateTime? ExpectedShipAtUtc { get; set; }
    public DateTime? EtsyCreatedAtUtc { get; set; }

    /// <summary>Etsy CDN URL of the item's photo, resolved when the order is stored.</summary>
    public string? ImageUrl { get; set; }
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
