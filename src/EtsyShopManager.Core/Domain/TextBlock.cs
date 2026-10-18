namespace EtsyShopManager.Core.Domain;

/// <summary>
/// A named, reusable piece of description text (e.g. "PLA care instructions"), placed in a listing's description as
/// <c>{{text.slug}}</c>, like a WordPress shortcode. The app keeps each description with its placeholders
/// (<see cref="ListingDescription"/>); Etsy gets the text with every placeholder replaced by its block's text. Editing a block
/// updates those descriptions as drafts, pushed through the normal reviewed push.
/// </summary>
public class TextBlock
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The placeholder name: lowercase letters, digits and hyphens (<c>{{text.pla-care}}</c>).</summary>
    public string Slug { get; set; } = "";
    /// <summary>The text, with \n newlines and no trailing whitespace on lines.</summary>
    public string Text { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public List<ListingTextBlock> Listings { get; set; } = [];
}

/// <summary>
/// The first version's link (text found by its exact copy). Kept only so links made before placeholders are turned into
/// placeholders once (<c>TextBlockService.ConvertLinksAsync</c>); not written any more.
/// </summary>
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

/// <summary>
/// A listing's description as the app keeps it, with text block placeholders, and the text it rendered to (what the listing's
/// description, and so Etsy, has). When the description changes outside the text (taking Etsy's version, restoring a
/// snapshot), <see cref="Rendered"/> no longer matches and the source is rebuilt from the description.
/// </summary>
public class ListingDescription
{
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;
    public string Source { get; set; } = "";
    public string Rendered { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
}
