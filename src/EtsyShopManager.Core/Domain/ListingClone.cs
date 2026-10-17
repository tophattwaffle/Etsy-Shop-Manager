namespace EtsyShopManager.Core.Domain;

public enum ListingCloneStatus { Pending = 0, Running = 1, Succeeded = 2, Failed = 3 }

/// <summary>
/// One copy of a listing into a new Etsy draft (same shop or the other one). <see cref="Log"/> lists every call made, so a
/// copy that stopped part way says what the draft on Etsy has and what it still lacks.
/// </summary>
public class ListingClone
{
    public int Id { get; set; }
    public int SourceListingId { get; set; }
    public Listing SourceListing { get; set; } = null!;
    public int TargetShopId { get; set; }
    public Shop TargetShop { get; set; } = null!;

    public ListingCloneStatus Status { get; set; }
    public string? RequestedBy { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>The choices made in the clone dialog (JSON: title, profiles, section).</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>The new draft on Etsy, once created (even if a later step failed).</summary>
    public long? NewEtsyListingId { get; set; }

    /// <summary>The new listing in this app, once imported.</summary>
    public int? NewListingId { get; set; }

    /// <summary>One line per call: what was sent and Etsy's answer.</summary>
    public string Log { get; set; } = "";
    public string? Error { get; set; }
    public long? JobId { get; set; }
}
