namespace EtsyShopManager.Core.Domain;

/// <summary>
/// A photo shared by several listings (a color swatch chart, a shop specials image), kept at a fixed position counted
/// from the end of each linked listing's photos. Its alt text is real, descriptive text and doubles as the fingerprint
/// that finds the template's photo on a listing.
/// </summary>
public class PhotoTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The image in the app's image store (content-hashed name; a new image gets a new name).</summary>
    public string FileName { get; set; } = "";
    public string AltText { get; set; } = "";
    /// <summary>1 = the last photo, 2 = second to last, …; the listing's own photos come first.</summary>
    public int RankFromEnd { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public List<ListingPhotoTemplate> Listings { get; set; } = [];
}

/// <summary>A listing using a photo template, and which version of it is on the listing.</summary>
public class ListingPhotoTemplate
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;
    public int PhotoTemplateId { get; set; }
    public PhotoTemplate PhotoTemplate { get; set; } = null!;

    /// <summary>The template image on the listing on Etsy (null: none yet). Etsy recompresses photos, so it can't be compared by content.</summary>
    public string? AppliedFileName { get; set; }
    /// <summary>The alt text the template's photo has on Etsy (found by it when the template's alt text changes).</summary>
    public string? AppliedAltText { get; set; }
    /// <summary>A template image in the listing's draft, waiting for a push.</summary>
    public string? PendingFileName { get; set; }
    public DateTime LinkedAtUtc { get; set; }
}
