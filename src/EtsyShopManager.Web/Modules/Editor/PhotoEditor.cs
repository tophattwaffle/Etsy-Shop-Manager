using System.Text.Json.Nodes;

namespace EtsyShopManager.Web.Modules.Editor;

public enum PhotoCallKind { Delete, Upload, Rank, AltText }

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
            PhotoCallKind.Upload => $"POST {path}  image={FileName}, rank={Rank}, overwrite=false{alt}  (add the new photo at position {Rank})",
            PhotoCallKind.Rank => $"POST {path}  listing_image_id={(ImageId > 0 ? ImageId : "(the new photo)")}, rank={Rank}, overwrite=false{alt}  ({photo} gets position {Rank})",
            _ => $"POST {path}  listing_image_id={ImageId}, rank={Rank}, overwrite=false{alt}  (alt text of {photo}, keeping position {Rank})",
        };
    }
}

/// <summary>
/// Photo editing on managed-state JSON (the "images" field: a list of {etsy_image_id, rank, alt_text}, plus file_name for
/// a photo added in the app and not on Etsy yet, which has etsy_image_id 0), and the calls a push makes.
/// <para>
/// What Etsy does was measured with the Admin photo order test (2026-10-17, three runs, docs/PLAN.md "Photos"): a photo's
/// rank is just a number. uploadListingImage with an existing listing_image_id and a rank gives that photo the rank and
/// renumbers nothing else, so ranks can tie (Etsy then shows the tied photos in no fixed order) and have gaps. With
/// overwrite=true the photo takes the slot and the photo shown at that rank drops off the listing, so the app never sends
/// it. A push therefore gives every photo its own rank 1 to n explicitly; reads show the change right away.
/// </para>
/// </summary>
public static class PhotoEditor
{
    public const int MaxPhotos = 20;
    public const int MaxAltTextLength = 500;

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
    /// The calls that turn Etsy's photos (<paramref name="etsy"/>, with their raw ranks) into <paramref name="local"/>:
    /// deletions, then uploads (each at its target rank), then every photo whose rank isn't yet its position gets it, so at
    /// the end each photo has its own rank 1 to n (Etsy never renumbers the others, so a single "move" leaves ties), then
    /// alt text for photos that didn't need a new rank. If every photo is replaced, the uploads go first so the listing is
    /// never without one.
    /// </summary>
    public static IReadOnlyList<PhotoCall> Plan(IReadOnlyList<(long Id, int Rank, string AltText)> etsy, JsonArray local)
    {
        var calls = new List<PhotoCall>();
        var wanted = local.Select((p, i) => (Id: (long?)p!["etsy_image_id"] is > 0 and long id ? id : -(i + 1L),
            Alt: (string?)p["alt_text"] ?? "", File: (string?)p["file_name"])).ToList();
        var rank = etsy.ToDictionary(e => e.Id, e => e.Rank);
        var deletes = etsy.Where(e => wanted.All(w => w.Id != e.Id)).Select(e => e.Id).ToList();
        var keptAny = wanted.Any(w => rank.ContainsKey(w.Id));

        void Delete()
        {
            foreach (var id in deletes)
            {
                calls.Add(new PhotoCall(PhotoCallKind.Delete, id, rank[id]));
                rank.Remove(id);
            }
        }
        void Upload()
        {
            foreach (var (w, i) in wanted.Select((w, i) => (w, i)).Where(x => x.w.Id < 0))
            {
                rank[w.Id] = i + 1;
                calls.Add(new PhotoCall(PhotoCallKind.Upload, w.Id, i + 1, w.Alt, w.File));
            }
        }
        if (keptAny) { Delete(); Upload(); }
        else { Upload(); Delete(); }

        var ranked = new HashSet<long>();
        foreach (var (w, i) in wanted.Select((w, i) => (w, i)))
        {
            if (rank[w.Id] == i + 1) continue;
            rank[w.Id] = i + 1;
            calls.Add(new PhotoCall(PhotoCallKind.Rank, w.Id, i + 1, w.Alt));
            ranked.Add(w.Id);
        }

        var etsyAlt = etsy.ToDictionary(e => e.Id, e => e.AltText);
        foreach (var (w, i) in wanted.Select((w, i) => (w, i)))
            if (w.Id > 0 && !ranked.Contains(w.Id) && etsyAlt.TryGetValue(w.Id, out var was) && was != w.Alt)
                calls.Add(new PhotoCall(PhotoCallKind.AltText, w.Id, i + 1, w.Alt));
        return calls;
    }

    /// <summary>Photos sharing a rank on Etsy (Etsy shows them in no fixed order), as groups of ids.</summary>
    public static IEnumerable<(int Rank, IReadOnlyList<long> Ids)> Ties(IReadOnlyList<(long Id, int Rank, string AltText)> etsy) =>
        etsy.GroupBy(e => e.Rank).Where(g => g.Count() > 1).Select(g => (g.Key, (IReadOnlyList<long>)[.. g.Select(e => e.Id)]));

    /// <summary>Etsy's photos as the plan needs them, from managed-state JSON.</summary>
    public static IReadOnlyList<(long Id, int Rank, string AltText)> FromManaged(JsonNode? images) =>
        [.. (images?.AsArray() ?? []).Select(p => ((long)p!["etsy_image_id"]!, (int?)p["rank"] ?? 0, (string?)p["alt_text"] ?? ""))];

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
