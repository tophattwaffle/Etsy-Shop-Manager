namespace EtsyShopManager.Core.Domain;

/// <summary>
/// A shared list of variation values (e.g. "PLA Colors"), used by several listings' variations. Changing the list (adding,
/// switching off or renaming an option) updates the linked listings as drafts, pushed through the normal reviewed push.
/// </summary>
public class VariationList
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The options in the list's order. Each keeps its id through renames.</summary>
    public List<VariationListOption> Options { get; set; } = [];
    public int NextOptionId { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public List<ListingVariationList> Listings { get; set; } = [];
}

/// <param name="Enabled">Off means "retired": linked listings keep the value but its combinations aren't available.</param>
public sealed record VariationListOption(int Id, string Name, bool Enabled);

/// <summary>
/// A listing's variation (by property) driven by a list, with the options as last applied to that listing. A variation can
/// follow several lists at once (e.g. "Basic Colors" then "Special Colors"); <see cref="Position"/> is their order.
/// </summary>
public class ListingVariationList
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;
    public int VariationListId { get; set; }
    public VariationList VariationList { get; set; } = null!;
    /// <summary>The listing's variation (Etsy property id, e.g. 513 or 200).</summary>
    public long PropertyId { get; set; }
    /// <summary>Where this list comes among the lists the variation follows (lowest first; ties by id).</summary>
    public int Position { get; set; }

    /// <summary>
    /// The list's options as this listing last reflected them (only those the listing has, under the name it has them).
    /// Changes to the list are worked out against this, so values the listing has of its own, or that were changed by hand,
    /// are left alone.
    /// </summary>
    public List<VariationListOption> AppliedOptions { get; set; } = [];
    public DateTime LinkedAtUtc { get; set; }
}
