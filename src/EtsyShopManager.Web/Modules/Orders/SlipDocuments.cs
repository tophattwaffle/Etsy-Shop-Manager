using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>A slip document as the Packing slips page lists it, with how many listings print it.</summary>
public sealed record SlipDocumentSummary(int Id, string Name, string? Heading, string? Url, int Listings, DateTime UpdatedAtUtc);

/// <summary>A listing linked to a slip document, or one that could be (the listing picker).</summary>
public sealed record SlipDocumentListing(int Id, long EtsyListingId, string Title, string ShopName, string State, string? LinkedTo);

/// <summary>
/// Instructions linked to listings (<see cref="SlipDocument"/>): saved on the Packing slips page, linked from there or from
/// a listing's page, and printed on the packing slip (and gift slip) of every order with a linked listing. A listing has
/// at most one; the same instructions can go on many listings. Local only: nothing here reaches Etsy.
/// </summary>
public sealed class SlipDocumentService(IDbContextFactory<AppDbContext> dbFactory, IAuditLog audit, TimeProvider time)
{
    public const int MaxNameLength = 100;
    public const int MaxHeadingLength = 100;
    public const int MaxCaptionLength = 100;
    public const int MaxUrlLength = 500;

    /// <summary>Kept short so the slip and its instructions fit on one page.</summary>
    public const int MaxTextLength = 1000;

    public async Task<List<SlipDocumentSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var counts = await db.ListingSlipDocuments.GroupBy(l => l.SlipDocumentId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return (await db.SlipDocuments.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct))
            .Select(d => new SlipDocumentSummary(d.Id, d.Name, d.Heading, d.Url, counts.GetValueOrDefault(d.Id), d.UpdatedAtUtc))
            .ToList();
    }

    public async Task<SlipDocument?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SlipDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    /// <summary>What's wrong with the document's fields, or null when it can be saved.</summary>
    public static string? Check(SlipDocument doc)
    {
        if (string.IsNullOrWhiteSpace(doc.Name)) return "Give the instructions a name.";
        if (doc.Name.Trim().Length > MaxNameLength) return $"Keep the name to {MaxNameLength} characters.";
        if (doc.Heading?.Trim().Length > MaxHeadingLength) return $"Keep the heading to {MaxHeadingLength} characters.";
        if (doc.Text.Trim().Length > MaxTextLength) return $"Keep the text to {MaxTextLength} characters, so the slip still fits on one page.";
        if (doc.QrCaption?.Trim().Length > MaxCaptionLength) return $"Keep the QR code caption to {MaxCaptionLength} characters.";
        if (Blank(doc.Url) is { } url)
        {
            if (url.Length > MaxUrlLength) return $"Keep the link to {MaxUrlLength} characters.";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                return "The link needs to be a web address starting with https://.";
        }
        if (Blank(doc.Text) is null && Blank(doc.Url) is null) return "Add some text or a link for the QR code.";
        return null;
    }

    /// <summary>Saves new instructions (Id 0) or changes to them. Returns the id, or what's wrong instead.</summary>
    public async Task<(int Id, string? Error)> SaveAsync(SlipDocument doc, string? user, CancellationToken ct = default)
    {
        if (Check(doc) is { } problem) return (0, problem);
        var name = doc.Name.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.SlipDocuments.AnyAsync(d => d.Id != doc.Id && d.Name == name, ct))
            return (0, $"There are already instructions called {name}.");

        var row = doc.Id == 0 ? null : await db.SlipDocuments.FirstOrDefaultAsync(d => d.Id == doc.Id, ct);
        if (doc.Id != 0 && row is null) return (0, "Those instructions were deleted.");
        if (row is null) db.SlipDocuments.Add(row = new SlipDocument());
        row.Name = name;
        row.Heading = Blank(doc.Heading);
        row.Text = doc.Text.Trim();
        row.Url = Blank(doc.Url);
        row.QrCaption = row.Url is null ? null : Blank(doc.QrCaption);
        row.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        row.UpdatedBy = user;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(doc.Id == 0 ? "slip-document.created" : "slip-document.updated", nameof(SlipDocument), row.Id.ToString(), name, user, ct);
        return (row.Id, null);
    }

    /// <summary>Deletes instructions; the listings that printed them print none.</summary>
    public async Task DeleteAsync(int id, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlipDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row is null) return;
        db.SlipDocuments.Remove(row); // the links go with it (cascade)
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("slip-document.deleted", nameof(SlipDocument), id.ToString(), row.Name, user, ct);
    }

    /// <summary>The instructions a listing prints, or null.</summary>
    public async Task<int?> GetLinkedAsync(int listingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ListingSlipDocuments.Where(l => l.ListingId == listingId).Select(l => (int?)l.SlipDocumentId).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Links the listings to the instructions (null: unlinks them). A listing has one at most, so linking replaces what it
    /// printed before. Returns what's wrong instead, if anything.
    /// </summary>
    public async Task<string?> LinkAsync(IReadOnlyCollection<int> listingIds, int? documentId, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        string? name = null;
        if (documentId is { } id && (name = await db.SlipDocuments.Where(d => d.Id == id).Select(d => d.Name).FirstOrDefaultAsync(ct)) is null)
            return "Those instructions were deleted.";
        var now = time.GetUtcNow().UtcDateTime;
        var existing = await db.ListingSlipDocuments.Where(l => listingIds.Contains(l.ListingId)).ToDictionaryAsync(l => l.ListingId, ct);
        var known = await db.Listings.Where(l => listingIds.Contains(l.Id)).Select(l => l.Id).ToListAsync(ct);
        foreach (var listingId in known)
        {
            existing.TryGetValue(listingId, out var link);
            if (documentId is null)
            {
                if (link is not null) db.ListingSlipDocuments.Remove(link);
                continue;
            }
            if (link is null) db.ListingSlipDocuments.Add(link = new ListingSlipDocument { ListingId = listingId });
            link.SlipDocumentId = documentId.Value;
            link.UpdatedAtUtc = now;
            link.UpdatedBy = user;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(documentId is null ? "slip-document.unlinked" : "slip-document.linked", nameof(SlipDocument),
            documentId?.ToString(), $"{name ?? "none"}: listings {string.Join(", ", known)}", user, ct);
        return null;
    }

    /// <summary>The listings that print the instructions.</summary>
    public async Task<List<SlipDocumentListing>> LinkedListingsAsync(int documentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ListingSlipDocuments.AsNoTracking().Where(l => l.SlipDocumentId == documentId)
            .OrderBy(l => l.Listing.Title)
            .Select(l => new SlipDocumentListing(l.ListingId, l.Listing.EtsyListingId, l.Listing.Title, l.Listing.Shop.Name, l.Listing.State, null))
            .ToListAsync(ct);
    }

    /// <summary>Listings whose title or Etsy id matches the search (for linking), with the instructions each prints now.</summary>
    public async Task<List<SlipDocumentListing>> SearchListingsAsync(string? search, int max = 20, CancellationToken ct = default)
    {
        var text = search?.Trim() ?? "";
        if (text.Length == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Listings.AsNoTracking();
        query = long.TryParse(text, out var etsyId)
            ? query.Where(l => l.EtsyListingId == etsyId || EF.Functions.Like(l.Title, $"%{text}%"))
            : query.Where(l => EF.Functions.Like(l.Title, $"%{text.Replace("%", "").Replace("_", "")}%"));
        return await query.OrderBy(l => l.Title).Take(max)
            .Select(l => new SlipDocumentListing(l.Id, l.EtsyListingId, l.Title, l.Shop.Name, l.State,
                db.ListingSlipDocuments.Where(s => s.ListingId == l.Id).Select(s => s.SlipDocument.Name).FirstOrDefault()))
            .ToListAsync(ct);
    }

    /// <summary>The instructions each of these Etsy listings prints (listings without any are left out), for a printout.</summary>
    public async Task<Dictionary<long, SlipDocument>> ForEtsyListingsAsync(IEnumerable<long> etsyListingIds, CancellationToken ct = default)
    {
        var ids = etsyListingIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ListingSlipDocuments.AsNoTracking().Where(l => ids.Contains(l.Listing.EtsyListingId))
            .Select(l => new { l.Listing.EtsyListingId, l.SlipDocument })
            .ToDictionaryAsync(l => l.EtsyListingId, l => l.SlipDocument, ct);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
