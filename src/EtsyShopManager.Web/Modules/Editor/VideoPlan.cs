using System.Text.Json.Nodes;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>
/// Video editing on managed-state JSON (the "videos" field: a list of {etsy_video_id, video_state, width, height}, plus
/// {file_name, name} for a video added in the app and not on Etsy yet), and the calls a push makes.
/// <para>
/// Etsy keeps a listing's videos in upload order and its API has no rank for them (seen 2026-10-25: a second video added
/// in Etsy's seller app came back second). So the app can't reorder: a list is always the Etsy videos it keeps, in
/// Etsy's order, followed by the new ones, which is the order Etsy has after a push (deletes first, then uploads).
/// </para>
/// </summary>
public static class VideoPlan
{
    /// <summary>Etsy's seller app allows two videos per listing.</summary>
    public const int MaxVideos = 2;

    public static List<JsonObject> Items(JsonNode? videos) =>
        videos is JsonArray a ? a.OfType<JsonObject>().ToList() : [];

    public static long? EtsyId(JsonNode video) =>
        video["etsy_video_id"] is JsonValue v && v.TryGetValue<long>(out var id) && id > 0 ? id : null;

    public static string? FileName(JsonNode video) => (string?)video["file_name"] is { Length: > 0 } f ? f : null;

    /// <summary>The list in the only order Etsy can hold: kept Etsy videos first (as given), then new ones.</summary>
    public static JsonArray Normalize(IEnumerable<JsonObject> videos)
    {
        var list = videos.ToList();
        return [.. list.Where(v => EtsyId(v) is not null).Concat(list.Where(v => EtsyId(v) is null)).Select(v => v.DeepClone())];
    }

    /// <summary>The list without the video at <paramref name="index"/>.</summary>
    public static JsonArray Remove(JsonNode? videos, int index) =>
        Normalize(Items(videos).Where((_, i) => i != index));

    /// <summary>
    /// The list with a new video, which goes after the Etsy videos (Etsy adds uploads last). Replacing a video takes it
    /// out first, so the new one doesn't keep the old one's place unless it was already last.
    /// </summary>
    public static JsonArray Add(JsonNode? videos, JsonObject video, int? replacing = null) =>
        Normalize(Items(videos).Where((_, i) => i != replacing).Append(video));

    /// <summary>What a push does: delete the Etsy videos the list no longer has, then upload the new ones in order.</summary>
    public static (IReadOnlyList<long> Deletes, IReadOnlyList<JsonObject> Uploads) Plan(JsonNode? etsy, JsonNode? local)
    {
        var keep = Items(local).Select(EtsyId).OfType<long>().ToHashSet();
        var deletes = Items(etsy).Select(EtsyId).OfType<long>().Where(id => !keep.Contains(id)).ToList();
        var uploads = Items(local).Where(v => EtsyId(v) is null && FileName(v) is not null).ToList();
        return (deletes, uploads);
    }

    /// <summary>
    /// After a push and a fresh read: Etsy holds the list when it has the kept videos in place and a new Etsy video
    /// wherever the list had a new upload.
    /// </summary>
    public static bool Applied(JsonNode? local, JsonNode? etsy)
    {
        var mine = Items(local);
        var theirs = Items(etsy);
        if (mine.Count != theirs.Count) return false;
        var kept = mine.Select(EtsyId).OfType<long>().ToHashSet();
        return mine.Zip(theirs).All(p => EtsyId(p.First) is { } id
            ? EtsyId(p.Second) == id
            : EtsyId(p.Second) is { } added && !kept.Contains(added));
    }
}
