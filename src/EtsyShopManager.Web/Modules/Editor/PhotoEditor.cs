using System.Text.Json.Nodes;

namespace EtsyShopManager.Web.Modules.Editor;

public enum PhotoCallKind { Delete, Upload, Move, AltText }

/// <summary>
/// One call of a photo push. <see cref="ImageId"/> is negative for a photo being uploaded in this push (its id is only
/// known once Etsy answers the upload).
/// </summary>
public sealed record PhotoCall(PhotoCallKind Kind, long ImageId, int Rank, string? AltText = null, string? FileName = null)
{
    public string Describe(long etsyShopId, long etsyListingId)
    {
        var path = $"/v3/application/shops/{etsyShopId}/listings/{etsyListingId}/images";
        var photo = ImageId > 0 ? $"photo #{ImageId}" : "the new photo";
        var alt = string.IsNullOrEmpty(AltText) ? "" : $", alt_text={AltText}";
        return Kind switch
        {
            PhotoCallKind.Delete => $"DELETE {path}/{ImageId}  (take photo #{ImageId} off the listing)",
            PhotoCallKind.Upload => $"POST {path}  image={FileName}, rank={Rank}, overwrite=false{alt}  (add it at the end)",
            PhotoCallKind.Move => $"POST {path}  listing_image_id={(ImageId > 0 ? ImageId : "(the new photo)")}, rank={Rank}, overwrite=false{alt}  (move {photo} to position {Rank})",
            _ => $"POST {path}  listing_image_id={ImageId}, rank={Rank}, overwrite=false{alt}  (alt text of {photo}, staying at position {Rank})",
        };
    }
}

/// <summary>
/// Photo editing on managed-state JSON (the "images" field: a list of {etsy_image_id, rank, alt_text}, plus file_name for
/// a photo added in the app and not on Etsy yet, which has etsy_image_id 0), and the calls a push makes.
/// <para>
/// What Etsy does was measured with the Admin photo order test (2026-10-17, docs/PLAN.md "Photos"): uploadListingImage
/// with an existing listing_image_id, a rank and overwrite=false <b>moves</b> that photo to the rank and shifts the others;
/// with overwrite=true it <b>replaces</b> the photo at that rank, which drops it from the listing, so the app never sends
/// overwrite=true. Etsy's reads show the new order only after up to ~2 minutes, so the plan works out each step from the
/// previous ones instead of reading back, and the push waits before checking the result.
/// </para>
/// </summary>
public static class PhotoEditor
{
    public const int MaxPhotos = 20;
    public const int MaxAltTextLength = 500;

    /// <summary>How long a push waits after its photo calls before reading the listing back (measured: up to ~2 minutes).</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(120);

    public static JsonArray Move(JsonArray images, int from, int to)
    {
        var list = Items(images);
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(Math.Clamp(to, 0, list.Count), item);
        return Numbered(list);
    }

    public static JsonArray Remove(JsonArray images, int index)
    {
        var list = Items(images);
        list.RemoveAt(index);
        return Numbered(list);
    }

    public static JsonArray SetAltText(JsonArray images, int index, string? altText)
    {
        var list = Items(images);
        list[index]["alt_text"] = (altText ?? "").Trim();
        return Numbered(list);
    }

    /// <summary>Adds a photo already saved in the image store, at the end.</summary>
    public static JsonArray Add(JsonArray images, string fileName, string? altText = null)
    {
        var list = Items(images);
        list.Add(new JsonObject
        {
            ["etsy_image_id"] = 0L,
            ["rank"] = list.Count + 1,
            ["alt_text"] = (altText ?? "").Trim(),
            ["file_name"] = fileName,
        });
        return Numbered(list);
    }

    /// <summary>
    /// The calls that turn Etsy's photos (<paramref name="etsy"/>, in order) into <paramref name="local"/>: deletions, then
    /// uploads at the end, then moves in target order (position 1 first, skipping photos already in place), then alt text
    /// for photos that didn't move. If every photo is replaced, the uploads go first so the listing is never without one.
    /// </summary>
    public static IReadOnlyList<PhotoCall> Plan(IReadOnlyList<(long Id, string AltText)> etsy, JsonArray local)
    {
        var calls = new List<PhotoCall>();
        var wanted = local.Select((p, i) => (Id: (long?)p!["etsy_image_id"] is > 0 and long id ? id : -(i + 1L),
            Alt: (string?)p["alt_text"] ?? "", File: (string?)p["file_name"])).ToList();
        var current = etsy.Select(e => e.Id).ToList();
        var deletes = current.Where(id => wanted.All(w => w.Id != id)).ToList();
        var uploads = wanted.Where(w => w.Id < 0).ToList();
        var keptAny = wanted.Any(w => w.Id > 0 && current.Contains(w.Id));

        void Delete()
        {
            foreach (var id in deletes)
            {
                calls.Add(new PhotoCall(PhotoCallKind.Delete, id, current.IndexOf(id) + 1));
                current.Remove(id);
            }
        }
        void Upload()
        {
            foreach (var u in uploads)
            {
                current.Add(u.Id);
                calls.Add(new PhotoCall(PhotoCallKind.Upload, u.Id, current.Count, u.Alt, u.File));
            }
        }
        if (keptAny) { Delete(); Upload(); }
        else { Upload(); Delete(); }

        var moved = new HashSet<long>();
        for (var i = 0; i < wanted.Count; i++)
        {
            if (current.IndexOf(wanted[i].Id) == i) continue;
            current.Remove(wanted[i].Id);
            current.Insert(i, wanted[i].Id);
            calls.Add(new PhotoCall(PhotoCallKind.Move, wanted[i].Id, i + 1, wanted[i].Alt));
            moved.Add(wanted[i].Id);
        }

        var etsyAlt = etsy.ToDictionary(e => e.Id, e => e.AltText);
        foreach (var (w, i) in wanted.Select((w, i) => (w, i)))
            if (w.Id > 0 && !moved.Contains(w.Id) && etsyAlt.TryGetValue(w.Id, out var was) && was != w.Alt)
                calls.Add(new PhotoCall(PhotoCallKind.AltText, w.Id, i + 1, w.Alt));
        return calls;
    }

    /// <summary>Etsy's photos as the plan needs them, from managed-state JSON.</summary>
    public static IReadOnlyList<(long Id, string AltText)> FromManaged(JsonNode? images) =>
        [.. (images?.AsArray() ?? []).Select(p => ((long)p!["etsy_image_id"]!, (string?)p["alt_text"] ?? ""))];

    /// <summary>File names of photos added in the app and not on Etsy yet (kept by the image store's cleanup).</summary>
    public static IEnumerable<string> PendingFiles(JsonNode? images) =>
        (images?.AsArray() ?? []).Select(p => (string?)p?["file_name"]).OfType<string>();

    /// <summary>After a push: the uploaded photos' new Etsy ids replace their local file references.</summary>
    public static JsonArray AdoptUploads(JsonArray local, IReadOnlyDictionary<string, long> uploaded)
    {
        var list = Items(local);
        foreach (var p in list)
            if ((string?)p["file_name"] is { } file && uploaded.TryGetValue(file, out var id))
            {
                p["etsy_image_id"] = id;
                p.Remove("file_name");
            }
        return Numbered(list);
    }

    private static List<JsonObject> Items(JsonArray images) => [.. images.Select(p => (JsonObject)p!.DeepClone())];

    private static JsonArray Numbered(List<JsonObject> list)
    {
        for (var i = 0; i < list.Count; i++) list[i]["rank"] = i + 1;
        return new JsonArray([.. list]);
    }
}
