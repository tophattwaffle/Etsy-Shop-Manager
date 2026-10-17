namespace EtsyShopManager.Core.Domain;

public enum PhotoOrderTestStatus { Pending = 0, Running = 1, Completed = 2, Stopped = 3, Refused = 4 }

/// <summary>
/// One run of the Admin photo order test: a scripted series of "put photo X at position N" calls on a listing that
/// isn't public, with the photo order read back after each call, to learn how Etsy reorders photos before the editor
/// relies on it. <see cref="ResultJson"/> holds every request, Etsy's answer and each read-back.
/// </summary>
public class PhotoOrderTest
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;

    public PhotoOrderTestStatus Status { get; set; }
    public string? RequestedBy { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>The steps (JSON list of {listing_image_id, rank, overwrite, send_alt_text}).</summary>
    public string StepsJson { get; set; } = "[]";

    /// <summary>What happened (JSON): the order before, each step's request, Etsy's answer and the orders read back.</summary>
    public string? ResultJson { get; set; }

    /// <summary>One line: completed, where it stopped and why, or why it was refused.</summary>
    public string? Outcome { get; set; }

    public int? SnapshotId { get; set; }
    public long? JobId { get; set; }
}
