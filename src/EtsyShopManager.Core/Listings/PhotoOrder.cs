using System.Text.Json.Nodes;

namespace EtsyShopManager.Core.Listings;

/// <summary>
/// Compares two versions of a listing's photos (the "images" field) the way a buyer sees them: the same photos, alt text
/// and order. A photo's rank is just a number on Etsy and Etsy renumbers on its own (closing gaps after a delete, later
/// replacing ties and gaps with 1 to n), so rank values themselves don't count. Photos sharing a rank have no fixed order.
/// </summary>
public static class PhotoOrder
{
    /// <summary>
    /// True when <paramref name="other"/> holds the same photos as <paramref name="etsy"/> and every pair of photos that
    /// <paramref name="other"/> puts in a definite order is in that order on Etsy too. A tie in <paramref name="other"/>
    /// (a base read while Etsy had one) accepts either order; a tie on Etsy doesn't satisfy a definite order, because Etsy
    /// then shows those photos in no fixed order.
    /// </summary>
    public static bool Same(JsonNode? etsy, JsonNode? other)
    {
        if (JsonNode.DeepEquals(etsy, other)) return true;
        if (etsy is not JsonArray e || other is not JsonArray o || e.Count != o.Count) return false;
        var etsyPhotos = Photos(e);
        var otherPhotos = Photos(o);
        if (etsyPhotos.Select(p => p.Key).Distinct().Count() != etsyPhotos.Count) return false;
        var etsyRank = etsyPhotos.ToDictionary(p => p.Key, p => p.Rank);
        if (otherPhotos.Any(p => !etsyRank.ContainsKey(p.Key))) return false;
        foreach (var x in otherPhotos)
            foreach (var y in otherPhotos)
                if (x.Rank < y.Rank && etsyRank[x.Key] >= etsyRank[y.Key]) return false;
        return true;
    }

    /// <summary>Each photo without its rank (id, alt text and anything else it carries), and its rank.</summary>
    private static List<(string Key, int Rank)> Photos(JsonArray images) =>
    [
        .. images.Select(p =>
        {
            var photo = (JsonObject)p!.DeepClone();
            var rank = (int?)photo["rank"] ?? 0;
            photo.Remove("rank");
            return (photo.ToJsonString(), rank);
        }),
    ];
}
