using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Templates;

public enum PhotoTemplateUseStatus
{
    UpToDate,
    /// <summary>Applying adds, replaces, moves or relabels the template's photo.</summary>
    HasChanges,
    /// <summary>The listing's draft has the template's photo; it's on Etsy after the next push.</summary>
    WaitingForPush,
    /// <summary>Adding the photo would take the listing over Etsy's 20 photos.</summary>
    TooManyPhotos,
    /// <summary>The listing's photos changed on Etsy since the last sync; resolve that in the editor first.</summary>
    ChangedOnEtsy,
}

/// <summary>A listing's photos after applying its photo templates (pure result).</summary>
/// <param name="Images">The new local photos (managed-state JSON), or the given ones unchanged.</param>
/// <param name="Changes">Per template id, what applying does in words; templates with nothing to do are absent.</param>
/// <param name="Uploads">Per template id, the template image added as a new photo.</param>
public sealed record PhotoTemplatePlan(JsonArray Images, IReadOnlyDictionary<int, string> Changes, IReadOnlyDictionary<int, string> Uploads, bool TooMany);

public sealed record PhotoTemplateUse(int ListingId, string Title, int ShopId, string ShopName, long EtsyListingId,
    string State, bool IsMissingOnEtsy, IReadOnlyList<string> Skus, IReadOnlyList<string> Tags,
    PhotoTemplateUseStatus Status, string? Change, bool PhotosPending) : ITemplateListingItem;

public sealed record PhotoTemplateSummary(int Id, string Name, string FileName, int RankFromEnd, int Listings, int WithChanges, int NeedAttention,
    DateTime UpdatedAtUtc);

/// <param name="HasPhoto">The listing already has a photo with the template's alt text (it becomes the template's photo when linked).</param>
public sealed record PhotoTemplateCandidate(int ListingId, string Title, int ShopId, string ShopName, long EtsyListingId,
    string State, bool IsMissingOnEtsy, IReadOnlyList<string> Skus, IReadOnlyList<string> Tags, bool HasPhoto) : ITemplateListingItem;

/// <summary>
/// Photo templates (Phase 4, photo templates). A template is one image with fixed alt text and a position counted from the
/// end: a listing's own photos come first, then its templates (the one with position 1 last). The template's photo on a
/// listing is found by its alt text (the fingerprint the plan describes), so nothing else needs to be stored on Etsy.
/// Applying writes the listing's new photo order as a draft (new image: uploaded, the old copy deleted; moved or relabelled
/// as needed); pushing is the normal photo push, which gives every photo its own rank 1..n with overwrite off. A listing
/// already in the right order is left exactly as it is, whatever Etsy's raw ranks.
/// </summary>
public sealed class PhotoTemplateService(IDbContextFactory<AppDbContext> dbFactory, ListingEditor editor, ImageStore images, IAuditLog audit,
    TimeProvider time)
{
    public const int MaxNameLength = 80;

    /// <summary>
    /// The photos after applying every template linked to one listing. <paramref name="local"/> is the listing's local photos;
    /// <paramref name="templates"/> its linked templates with their link rows.
    /// </summary>
    public static PhotoTemplatePlan Plan(JsonArray local, IReadOnlyList<(PhotoTemplate Template, ListingPhotoTemplate Link)> templates)
    {
        var items = local.Select(p => p!.AsObject()).ToList();
        var claimed = Claim(items, templates);

        var changes = new Dictionary<int, string>();
        var uploads = new Dictionary<int, string>();
        var result = items.Where(p => !claimed.ContainsValue(p)).Select(p => (JsonObject)p.DeepClone()).ToList();
        foreach (var (t, link) in templates.OrderByDescending(x => x.Template.RankFromEnd).ThenBy(x => x.Template.Id))
        {
            claimed.TryGetValue(t.Id, out var photo);
            var current = photo is not null && ((string?)photo["file_name"] == t.FileName
                                                || (long?)photo["etsy_image_id"] is > 0 && link.AppliedFileName == t.FileName);
            JsonObject entry;
            if (current)
            {
                entry = (JsonObject)photo!.DeepClone();
                if ((string?)entry["alt_text"] != t.AltText)
                {
                    entry["alt_text"] = t.AltText;
                    changes[t.Id] = "Updates the photo's alt text";
                }
            }
            else
            {
                entry = new JsonObject { ["etsy_image_id"] = 0L, ["rank"] = 0, ["alt_text"] = t.AltText, ["file_name"] = t.FileName };
                uploads[t.Id] = t.FileName;
                changes[t.Id] = photo is null ? "Adds the photo" : "Replaces the photo with the template's new image";
            }
            result.Add(entry);
        }

        // Unchanged order and photos: keep the listing exactly as it is (Etsy's raw ranks may have gaps; only order counts).
        static string Key(JsonObject p) => $"{(long?)p["etsy_image_id"]}|{(string?)p["file_name"]}|{(string?)p["alt_text"]}";
        if (result.Select(Key).SequenceEqual(items.Select(Key)))
            return new PhotoTemplatePlan(local, changes, uploads, false);

        foreach (var (t, _) in templates.Where(x => !changes.ContainsKey(x.Template.Id)))
            if (claimed.TryGetValue(t.Id, out var photo) && items.IndexOf(photo) != result.FindIndex(p => Key(p) == Key(photo)))
                changes[t.Id] = "Moves the photo into its place";
        var numbered = new JsonArray([.. result.Select((p, i) => { p["rank"] = i + 1; return (JsonNode)p; })]);
        return new PhotoTemplatePlan(numbered, changes, uploads, result.Count > PhotoEditor.MaxPhotos);
    }

    /// <summary>Each linked template's photo among the listing's photos (template id -> the photo), by its file or alt text.</summary>
    private static Dictionary<int, JsonObject> Claim(List<JsonObject> items, IReadOnlyList<(PhotoTemplate Template, ListingPhotoTemplate Link)> templates)
    {
        var claimed = new Dictionary<int, JsonObject>();
        foreach (var (t, link) in templates)
        {
            var photo = items.FirstOrDefault(p => !claimed.ContainsValue(p) && (
                (string?)p["file_name"] is { } file && (file == t.FileName || file == link.PendingFileName)
                || (string?)p["alt_text"] is { Length: > 0 } alt && (alt == t.AltText || alt == link.AppliedAltText)));
            if (photo is not null) claimed[t.Id] = photo;
        }
        return claimed;
    }

    /// <summary>The photos that come from a linked template (photo index -> its template), found as <see cref="Plan"/> finds them.</summary>
    public static IReadOnlyDictionary<int, PhotoTemplate> TemplatePhotos(JsonArray photos,
        IReadOnlyList<(PhotoTemplate Template, ListingPhotoTemplate Link)> templates)
    {
        var items = photos.Select(p => p!.AsObject()).ToList();
        var byId = templates.ToDictionary(x => x.Template.Id, x => x.Template);
        return Claim(items, templates).ToDictionary(c => items.IndexOf(c.Value), c => byId[c.Key]);
    }

    /// <summary>
    /// Why a push must wait: a linked template's photo is on Etsy but no longer in the listing's local photos. A template's
    /// photo only leaves a listing by unlinking it (<see cref="UnlinkAsync"/>), never by removing it in the editor.
    /// </summary>
    public static IEnumerable<string> RemovalBlockers(JsonArray etsy, JsonArray local,
        IReadOnlyList<(PhotoTemplate Template, ListingPhotoTemplate Link)> templates)
    {
        var onListing = TemplatePhotos(local, templates).Values.Select(t => t.Id).ToHashSet();
        return TemplatePhotos(etsy, templates).Values.Where(t => !onListing.Contains(t.Id))
            .Select(t => $"The photo from the photo template \"{t.Name}\" was removed. It stays while the listing is linked to the template: " +
                         "put it back (discard the photo edits), or use Unlink on that photo to take it off.");
    }

    public async Task<IReadOnlyList<PhotoTemplateSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var templates = await db.PhotoTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var result = new List<PhotoTemplateSummary>();
        foreach (var t in templates)
        {
            var uses = await UsesAsync(db, t.Id, ct);
            result.Add(new PhotoTemplateSummary(t.Id, t.Name, t.FileName, t.RankFromEnd, uses.Count,
                uses.Count(u => u.Status == PhotoTemplateUseStatus.HasChanges),
                uses.Count(u => u.Status is PhotoTemplateUseStatus.TooManyPhotos or PhotoTemplateUseStatus.ChangedOnEtsy), t.UpdatedAtUtc));
        }
        return result;
    }

    public async Task<PhotoTemplate?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PhotoTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    /// <summary>Creates or updates a template (the image is a file already saved in the image store). Listings change only when applied.</summary>
    public async Task<(int? Id, string? Error)> SaveAsync(int? id, string name, string? fileName, string altText, int rankFromEnd, string? user,
        CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        altText = (altText ?? "").Trim();
        if (name.Length == 0) return (null, "Give the template a name.");
        if (name.Length > MaxNameLength) return (null, $"The name is {name.Length} characters; keep it to {MaxNameLength}.");
        if (string.IsNullOrEmpty(fileName) || !images.Exists(fileName)) return (null, "Add the template's image.");
        if (altText.Length == 0) return (null, "Describe the image in the alt text: buyers using screen readers hear it, and it identifies the photo on each listing.");
        if (altText.Length > PhotoEditor.MaxAltTextLength) return (null, $"The alt text is {altText.Length} characters; Etsy allows {PhotoEditor.MaxAltTextLength}.");
        if (rankFromEnd < 1 || rankFromEnd > PhotoEditor.MaxPhotos) return (null, $"The position from the end must be 1 to {PhotoEditor.MaxPhotos}.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.PhotoTemplates.AnyAsync(t => t.Id != id && t.Name == name, ct)) return (null, $"There's already a template called \"{name}\".");
        if (await db.PhotoTemplates.AnyAsync(t => t.Id != id && t.AltText == altText, ct))
            return (null, "Another template has the same alt text; each template needs its own (it identifies the photo on listings).");
        var now = time.GetUtcNow().UtcDateTime;
        var template = id is { } existing ? await db.PhotoTemplates.FirstOrDefaultAsync(t => t.Id == existing, ct) : null;
        if (id is not null && template is null) return (null, "That template no longer exists.");
        if (template is null)
        {
            template = new PhotoTemplate { CreatedAtUtc = now };
            db.PhotoTemplates.Add(template);
        }
        (template.Name, template.FileName, template.AltText, template.RankFromEnd, template.UpdatedAtUtc, template.UpdatedBy) =
            (name, fileName, altText, rankFromEnd, now, user);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "photo-template.created" : "photo-template.saved", nameof(PhotoTemplate), template.Id.ToString(), name, user, ct);
        return (template.Id, null);
    }

    /// <summary>Deletes a template. Listings keep the photo; they're just no longer linked.</summary>
    public async Task DeleteAsync(int id, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var template = await db.PhotoTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null) return;
        db.PhotoTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("photo-template.deleted", nameof(PhotoTemplate), id.ToString(), $"{template.Name} (listings keep the photo)", user, ct);
    }

    public async Task<IReadOnlyList<PhotoTemplateUse>> UsesAsync(int templateId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await UsesAsync(db, templateId, ct);
    }

    private static async Task<List<PhotoTemplateUse>> UsesAsync(AppDbContext db, int templateId, CancellationToken ct)
    {
        var listingIds = await db.ListingPhotoTemplates.Where(l => l.PhotoTemplateId == templateId).Select(l => l.ListingId).ToListAsync(ct);
        var result = new List<PhotoTemplateUse>();
        foreach (var (listing, model, links) in await LoadListingsAsync(db, listingIds, ct))
        {
            var local = model.Local(ListingFields.Images) as JsonArray ?? [];
            var pending = !ListingFields.Same(ListingFields.Images, model.Etsy[ListingFields.Images], local);
            var mine = links.Single(l => l.Template.Id == templateId);
            PhotoTemplateUseStatus status;
            string? change = null;
            if (model.DriftFields.Contains(ListingFields.Images)) status = PhotoTemplateUseStatus.ChangedOnEtsy;
            else
            {
                var plan = Plan(local, links);
                change = plan.Changes.GetValueOrDefault(templateId);
                status = change is null
                    ? local.Any(p => (string?)p!["file_name"] == mine.Template.FileName) ? PhotoTemplateUseStatus.WaitingForPush : PhotoTemplateUseStatus.UpToDate
                    : plan.TooMany ? PhotoTemplateUseStatus.TooManyPhotos : PhotoTemplateUseStatus.HasChanges;
            }
            result.Add(new PhotoTemplateUse(listing.Id, listing.Title, listing.ShopId, listing.Shop.Name, listing.EtsyListingId,
                listing.State, listing.IsMissingOnEtsy, listing.Skus, listing.Tags, status, change, pending));
        }
        return [.. result.OrderBy(u => u.Status == PhotoTemplateUseStatus.UpToDate).ThenBy(u => u.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Listings with their edit model and all their linked templates (positions depend on each other).</summary>
    private static async Task<List<(Listing Listing, ListingEditModel Model, List<(PhotoTemplate Template, ListingPhotoTemplate Link)> Links)>> LoadListingsAsync(
        AppDbContext db, IReadOnlyCollection<int> listingIds, CancellationToken ct)
    {
        var listings = await db.Listings.AsNoTracking().Include(l => l.EtsyState).Include(l => l.Shop)
            .Where(l => listingIds.Contains(l.Id) && l.EtsyState != null).ToListAsync(ct);
        var links = await db.ListingPhotoTemplates.AsNoTracking().Include(l => l.PhotoTemplate)
            .Where(l => listingIds.Contains(l.ListingId)).ToListAsync(ct);
        return [.. listings.Select(l => (l, ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson),
            links.Where(x => x.ListingId == l.Id).Select(x => (x.PhotoTemplate, x)).ToList()))];
    }

    /// <summary>Listings not linked yet; those with a photo carrying the template's alt text first.</summary>
    public async Task<IReadOnlyList<PhotoTemplateCandidate>> CandidatesAsync(int templateId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var template = await db.PhotoTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct);
        if (template is null) return [];
        var linked = await db.ListingPhotoTemplates.Where(l => l.PhotoTemplateId == templateId).Select(l => l.ListingId).ToListAsync(ct);
        var listings = await db.Listings.AsNoTracking().Include(l => l.EtsyState).Include(l => l.Shop)
            .Where(l => !linked.Contains(l.Id) && l.EtsyState != null).ToListAsync(ct);
        return [.. listings.Select(l => new PhotoTemplateCandidate(l.Id, l.Title, l.ShopId, l.Shop.Name, l.EtsyListingId,
                l.State, l.IsMissingOnEtsy, l.Skus, l.Tags, HasPhoto(l, template.AltText)))
            .OrderByDescending(c => c.HasPhoto).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool HasPhoto(Listing l, string altText) =>
        (ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson).Local(ListingFields.Images) as JsonArray ?? [])
        .Any(p => (string?)p!["alt_text"] == altText);

    /// <summary>
    /// Links listings. One that already has a photo with the template's alt text keeps it as the template's photo (taken as
    /// the current image); the others get the photo on the next apply.
    /// </summary>
    public async Task<int> LinkAsync(int templateId, IEnumerable<int> listingIds, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var template = await db.PhotoTemplates.AsNoTracking().FirstAsync(t => t.Id == templateId, ct);
        var ids = listingIds.Distinct().ToList();
        var already = await db.ListingPhotoTemplates.Where(l => l.PhotoTemplateId == templateId && ids.Contains(l.ListingId)).Select(l => l.ListingId).ToListAsync(ct);
        var listings = await db.Listings.AsNoTracking().Include(l => l.EtsyState).Where(l => ids.Contains(l.Id) && !already.Contains(l.Id) && l.EtsyState != null).ToListAsync(ct);
        foreach (var l in listings)
        {
            var has = HasPhoto(l, template.AltText);
            db.ListingPhotoTemplates.Add(new ListingPhotoTemplate
            {
                ListingId = l.Id, PhotoTemplateId = templateId, LinkedAtUtc = time.GetUtcNow().UtcDateTime,
                AppliedFileName = has ? template.FileName : null, AppliedAltText = has ? template.AltText : null,
            });
        }
        await db.SaveChangesAsync(ct);
        if (listings.Count > 0) await audit.WriteAsync("photo-template.linked", nameof(PhotoTemplate), templateId.ToString(), $"{template.Name}: {listings.Count} listing(s)", user, ct);
        return listings.Count;
    }

    /// <summary>The photo templates one listing is linked to, with their link rows.</summary>
    public async Task<IReadOnlyList<(PhotoTemplate Template, ListingPhotoTemplate Link)>> ListingLinksAsync(int listingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ListingLinksAsync(db, listingId, ct);
    }

    internal static async Task<IReadOnlyList<(PhotoTemplate Template, ListingPhotoTemplate Link)>> ListingLinksAsync(AppDbContext db, int listingId,
        CancellationToken ct) =>
        [.. (await db.ListingPhotoTemplates.AsNoTracking().Include(l => l.PhotoTemplate).Where(l => l.ListingId == listingId).ToListAsync(ct))
            .Select(l => (l.PhotoTemplate, l))];

    /// <summary>
    /// Unlinks one listing from a template. With <paramref name="removePhoto"/> (Unlink on the photo in the editor) the
    /// template's photo also comes off the listing as an unpushed edit (the other photos keep their order; the push gives
    /// them positions 1..n); without it (the template's page) the listing keeps the photo as one of its own. The template
    /// and its other listings stay as they are. Returns whether the photo was removed.
    /// </summary>
    public async Task<bool> UnlinkAsync(int templateId, int listingId, string? user, bool removePhoto = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!removePhoto)
        {
            await db.ListingPhotoTemplates.Where(l => l.PhotoTemplateId == templateId && l.ListingId == listingId).ExecuteDeleteAsync(ct);
            await audit.WriteAsync("photo-template.unlinked", nameof(PhotoTemplate), templateId.ToString(), $"listing {listingId} (keeps the photo)", user, ct);
            return false;
        }
        return await UnlinkAndRemoveAsync(db, templateId, listingId, user, ct);
    }

    private async Task<bool> UnlinkAndRemoveAsync(AppDbContext db, int templateId, int listingId, string? user, CancellationToken ct)
    {
        var links = await ListingLinksAsync(db, listingId, ct);
        if (links.All(l => l.Template.Id != templateId)) return false;
        var listing = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstOrDefaultAsync(l => l.Id == listingId, ct);
        var local = listing?.EtsyState is null ? []
            : ListingEditModel.Parse(listing.EtsyState.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson).Local(ListingFields.Images) as JsonArray ?? [];
        var index = TemplatePhotos(local, links).Where(p => p.Value.Id == templateId).Select(p => (int?)p.Key).FirstOrDefault();

        await db.ListingPhotoTemplates.Where(l => l.PhotoTemplateId == templateId && l.ListingId == listingId).ExecuteDeleteAsync(ct);
        if (index is { } i)
            await editor.SaveAsync(listingId, new Dictionary<string, JsonNode?> { [ListingFields.Images] = PhotoEditor.Remove(local, i) }, user, ct);
        await audit.WriteAsync("photo-template.unlinked", nameof(PhotoTemplate), templateId.ToString(),
            $"listing {listingId} ({(index is null ? "it didn't have the photo" : "photo removed as an unpushed edit")})", user, ct);
        return index is not null;
    }

    /// <summary>
    /// Puts the template's photo in place on every linked listing that needs it, as a draft (nothing is sent to Etsy).
    /// Listings whose photos changed on Etsy, or that would go over 20 photos, are skipped. Returns the listings changed.
    /// </summary>
    public async Task<int> ApplyAsync(int templateId, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var template = await db.PhotoTemplates.AsNoTracking().FirstAsync(t => t.Id == templateId, ct);
        var listingIds = await db.ListingPhotoTemplates.Where(l => l.PhotoTemplateId == templateId).Select(l => l.ListingId).ToListAsync(ct);
        var changed = 0;
        foreach (var (listing, model, links) in await LoadListingsAsync(db, listingIds, ct))
        {
            if (model.DriftFields.Contains(ListingFields.Images)) continue;
            var plan = Plan(model.Local(ListingFields.Images) as JsonArray ?? [], links);
            if (plan.Changes.Count == 0 || plan.TooMany) continue;
            await editor.SaveAsync(listing.Id, new Dictionary<string, JsonNode?> { [ListingFields.Images] = plan.Images }, user, ct);
            foreach (var (tid, file) in plan.Uploads)
                await db.ListingPhotoTemplates.Where(l => l.ListingId == listing.Id && l.PhotoTemplateId == tid)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.PendingFileName, file), ct);
            changed++;
        }
        if (changed > 0)
            await audit.WriteAsync("photo-template.applied", nameof(PhotoTemplate), templateId.ToString(), $"{template.Name}: {changed} listing draft(s) updated", user, ct);
        return changed;
    }

    /// <summary>The review before pushing: each linked listing with unpushed photo edits, with the editor's own preview.</summary>
    public async Task<IReadOnlyList<TemplatePushItem>> ReviewAsync(int templateId, CancellationToken ct = default)
    {
        var items = new List<TemplatePushItem>();
        foreach (var use in (await UsesAsync(templateId, ct)).Where(u => u.PhotosPending))
            items.Add(new TemplatePushItem(use.ListingId, use.Title, use.ShopName, await editor.PreviewAsync(use.ListingId, ct)));
        return items;
    }

    public async Task<IReadOnlyList<(int ListingId, string Title, int? PushId, IReadOnlyList<string> Blockers)>> PushAsync(int templateId,
        string? user, CancellationToken ct = default)
    {
        var results = new List<(int, string, int?, IReadOnlyList<string>)>();
        foreach (var item in await ReviewAsync(templateId, ct))
        {
            var (pushId, blockers) = await editor.RequestPushAsync(item.ListingId, user, ct);
            results.Add((item.ListingId, item.Title, pushId, blockers));
        }
        return results;
    }

    /// <summary>
    /// After a push reached Etsy: a template image that was waiting in the draft is now the listing's (the push adopted the
    /// upload, so the draft no longer refers to the file), and the photo carries the template's alt text.
    /// </summary>
    public async Task RecordPushedAsync(int listingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listing = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing?.EtsyState is null) return;
        var model = ListingEditModel.Parse(listing.EtsyState.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
        var etsyPhotos = model.Etsy[ListingFields.Images] as JsonArray ?? [];
        var local = model.Local(ListingFields.Images) as JsonArray ?? [];
        var links = await db.ListingPhotoTemplates.Include(l => l.PhotoTemplate).Where(l => l.ListingId == listingId).ToListAsync(ct);
        foreach (var link in links)
        {
            var t = link.PhotoTemplate;
            if (etsyPhotos.All(p => (string?)p!["alt_text"] != t.AltText)) continue;
            link.AppliedAltText = t.AltText;
            if (link.PendingFileName is { } pending && local.All(p => (string?)p!["file_name"] != pending))
                (link.AppliedFileName, link.PendingFileName) = (pending, null);
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Keeps photo template links current after each push (see <see cref="PhotoTemplateService.RecordPushedAsync"/>).</summary>
public sealed class PhotoTemplatesOnListingPushed(PhotoTemplateService templates) : IDomainEventHandler<ListingPushed>
{
    public Task HandleAsync(ListingPushed e, CancellationToken ct) =>
        e.Fields.Contains(ListingFields.Images) ? templates.RecordPushedAsync(e.ListingId, ct) : Task.CompletedTask;
}
