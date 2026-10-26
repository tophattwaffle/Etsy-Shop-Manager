namespace EtsyShopManager.Core.Domain;

/// <summary>
/// Instructions linked to listings (Packing slips page): a heading, some text and a QR code to a link, printed on the
/// packing slip (and gift slip) of every order with one of those listings. Kept here only; Etsy never sees them.
/// </summary>
public class SlipDocument
{
    public int Id { get; set; }

    /// <summary>What it's called in the app (unique); not printed.</summary>
    public string Name { get; set; } = "";

    /// <summary>Printed above the text ("Care instructions"); optional.</summary>
    public string? Heading { get; set; }

    public string Text { get; set; } = "";

    /// <summary>The http(s) link the QR code points to; no QR code when empty.</summary>
    public string? Url { get; set; }

    /// <summary>Printed under the QR code ("Scan for the video"); optional.</summary>
    public string? QrCaption { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>The instructions a listing prints on its packing slips (one per listing).</summary>
public class ListingSlipDocument
{
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;

    public int SlipDocumentId { get; set; }
    public SlipDocument SlipDocument { get; set; } = null!;

    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}
