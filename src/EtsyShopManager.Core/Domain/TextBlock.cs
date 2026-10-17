namespace EtsyShopManager.Core.Domain;

/// <summary>
/// A named, reusable piece of description text (e.g. "PLA care instructions"). Listings that use it are linked through
/// <see cref="ListingTextBlock"/>; editing the block updates their descriptions as drafts, pushed through the normal
/// reviewed push. Nothing marks the block inside the description on Etsy: it is found by its text.
/// </summary>
public class TextBlock
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The text, with \n newlines and no trailing whitespace on lines.</summary>
    public string Text { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public List<ListingTextBlock> Listings { get; set; } = [];
}

/// <summary>A listing using a text block, with the block's text as the app last put it into the description.</summary>
public class ListingTextBlock
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;
    public int TextBlockId { get; set; }
    public TextBlock TextBlock { get; set; } = null!;

    /// <summary>
    /// The block's text as it is in this listing's description. When the block changes, this is what gets replaced; if the
    /// description no longer contains it (edited by hand, here or on Etsy), the listing is left alone and flagged.
    /// </summary>
    public string AppliedText { get; set; } = "";
    public DateTime LinkedAtUtc { get; set; }
}
