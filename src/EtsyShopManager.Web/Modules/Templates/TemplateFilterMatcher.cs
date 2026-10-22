namespace EtsyShopManager.Web.Modules.Templates;

/// <summary>
/// Represents common listing fields required to filter template listings.
/// </summary>
public interface ITemplateListingItem
{
    int ListingId { get; }
    string Title { get; }
    int ShopId { get; }
    string ShopName { get; }
    long EtsyListingId { get; }
    string State { get; }
    bool IsMissingOnEtsy { get; }
    IReadOnlyList<string> Skus { get; }
    IReadOnlyList<string> Tags { get; }
}

/// <summary>
/// Filter criteria for template listing tables.
/// </summary>
public sealed record TemplateListingFilter
{
    public string? Text { get; init; }
    public int? ShopId { get; init; }
    public string Status { get; init; } = "all";
}

/// <summary>
/// Matching logic for filtering template listings by search text, shop, and status.
/// </summary>
public static class TemplateFilterMatcher
{
    public static bool Matches(ITemplateListingItem item, TemplateListingFilter filter)
    {
        if (filter.ShopId is { } shopId && item.ShopId != shopId)
            return false;

        var status = (filter.Status ?? "all").Trim().ToLowerInvariant();
        var statusMatches = status switch
        {
            "all" => !item.IsMissingOnEtsy,
            "active" => !item.IsMissingOnEtsy && string.Equals(item.State, "active", StringComparison.OrdinalIgnoreCase),
            "draft" => !item.IsMissingOnEtsy && string.Equals(item.State, "draft", StringComparison.OrdinalIgnoreCase),
            "expired" => !item.IsMissingOnEtsy && string.Equals(item.State, "expired", StringComparison.OrdinalIgnoreCase),
            "sold_out" => !item.IsMissingOnEtsy && string.Equals(item.State, "sold_out", StringComparison.OrdinalIgnoreCase),
            "inactive" => !item.IsMissingOnEtsy && string.Equals(item.State, "inactive", StringComparison.OrdinalIgnoreCase),
            "missing" => item.IsMissingOnEtsy,
            _ => !item.IsMissingOnEtsy
        };

        if (!statusMatches)
            return false;

        var text = filter.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var isNumeric = long.TryParse(text, out var etsyId);
            var matchesEtsyId = isNumeric && item.EtsyListingId == etsyId;
            var matchesTitle = !string.IsNullOrEmpty(item.Title) && item.Title.Contains(text, StringComparison.OrdinalIgnoreCase);
            var matchesSku = item.Skus is not null && item.Skus.Any(s => !string.IsNullOrEmpty(s) && s.Contains(text, StringComparison.OrdinalIgnoreCase));
            var matchesTag = item.Tags is not null && item.Tags.Any(t => !string.IsNullOrEmpty(t) && t.Contains(text, StringComparison.OrdinalIgnoreCase));

            if (!matchesEtsyId && !matchesTitle && !matchesSku && !matchesTag)
                return false;
        }

        return true;
    }
}
