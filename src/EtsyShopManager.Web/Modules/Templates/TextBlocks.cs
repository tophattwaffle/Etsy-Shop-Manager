using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using MudBlazor;

namespace EtsyShopManager.Web.Modules.Templates;

public enum TextBlockUseStatus
{
    /// <summary>The description has the block's current text.</summary>
    UpToDate,
    /// <summary>The block changed since the description was last written; applying puts its new text in.</summary>
    WillChange,
    /// <summary>The description changed on Etsy since the last sync; resolve that in the editor first.</summary>
    ChangedOnEtsy,
}

/// <summary>A listing using a block: where it stands, and its description (as on Etsy) before and after applying the block.</summary>
public sealed record TextBlockUse(int ListingId, string Title, string ShopName, TextBlockUseStatus Status, string Before, string After,
    bool DescriptionPending);

public sealed record TextBlockSummary(int Id, string Name, string Slug, int Listings, int WillChange, int NeedAttention, DateTime UpdatedAtUtc);

/// <summary>A listing not using the block whose description already contains its text word for word.</summary>
public sealed record TextBlockCandidate(int ListingId, string Title, string ShopName);

/// <summary>What pushing a block's listings would do, per listing (the editor's own review).</summary>
public sealed record TextBlockPushItem(int ListingId, string Title, string ShopName, PushPreview Preview);

/// <summary>
/// Text block placeholders in descriptions: <c>{{text.slug}}</c>, like a WordPress shortcode. Pure helpers.
/// </summary>
public static partial class TextBlockMarkup
{
    public static string Token(string slug) => "{{text." + slug + "}}";

    /// <summary>The slugs used in a description (lowercase, each once, in order).</summary>
    public static IReadOnlyList<string> Slugs(string source) =>
        [.. Placeholder().Matches(source).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct()];

    /// <summary>The description as Etsy gets it: every placeholder replaced by its block's text. Unknown ones stay as written.</summary>
    public static (string Text, IReadOnlyList<string> Unknown) Render(string source, Func<string, string?> blockText)
    {
        var unknown = new List<string>();
        var text = Placeholder().Replace(source, m =>
        {
            var slug = m.Groups[1].Value.ToLowerInvariant();
            if (blockText(slug) is { } t) return t;
            if (!unknown.Contains(slug)) unknown.Add(slug);
            return m.Value;
        });
        return (text, unknown);
    }

    public static string Render(string source, IEnumerable<TextBlock> blocks)
    {
        var bySlug = blocks.ToDictionary(b => b.Slug);
        return Render(source, s => bySlug.GetValueOrDefault(s)?.Text).Text;
    }

    /// <summary>Puts placeholders back where a description has these blocks' text word for word (longest text first).</summary>
    public static string Tokenize(string text, IEnumerable<TextBlock> blocks) =>
        blocks.Where(b => b.Text.Length > 0).OrderByDescending(b => b.Text.Length)
            .Aggregate(text, (t, b) => t.Replace(b.Text, Token(b.Slug), StringComparison.Ordinal));

    /// <summary>Replaces one block's placeholders (e.g. by its text when the block is deleted, or by a renamed placeholder).</summary>
    public static string ReplaceSlug(string source, string slug, string replacement) =>
        Placeholder().Replace(source, m => m.Groups[1].Value.Equals(slug, StringComparison.OrdinalIgnoreCase) ? replacement : m.Value);

    public static bool IsValidSlug(string slug) => ValidSlug().IsMatch(slug);

    /// <summary>"PLA care (short)" → "pla-care-short".</summary>
    public static string MakeSlug(string name)
    {
        var slug = NotSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "block" : slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }

    [GeneratedRegex(@"\{\{\s*text\.([A-Za-z0-9-]+)\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex ValidSlug();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}

/// <summary>
/// Reusable description text (Phase 4, text templates), placed as <c>{{text.slug}}</c>. The app keeps each description with
/// its placeholders (<see cref="ListingDescription"/>, checked against the listing's description every time it is read);
/// the listing's description, and so Etsy, always has the blocks' text. A listing can't change a block's text: only the
/// block can. Editing a block changes nothing on its own: applying it writes the new descriptions as drafts, and pushing
/// goes through <see cref="ListingEditor"/>'s reviewed push, listing by listing.
/// </summary>
public sealed class TextBlockService(IDbContextFactory<AppDbContext> dbFactory, ListingEditor editor, IAuditLog audit, TimeProvider time,
    ILogger<TextBlockService> logger)
{
    public const int MaxNameLength = 80;
    public const int MaxSlugLength = 60;

    /// <summary>Block text as stored and compared: \n newlines, no trailing spaces on lines, no blank lines around it.</summary>
    public static string Normalize(string? text) =>
        string.Join("\n", (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd())).Trim('\n', ' ');

    /// <summary>
    /// A listing's description with its placeholders. When the description no longer is what the saved source rendered to
    /// (taken from Etsy, a snapshot restored, edits discarded), it is rebuilt: the blocks the source used are put back as
    /// placeholders wherever the description still has their text word for word.
    /// </summary>
    public static string Source(ListingDescription? saved, string description, IReadOnlyCollection<TextBlock> blocks)
    {
        if (saved is null) return description;
        if (saved.Rendered == description) return saved.Source;
        var used = TextBlockMarkup.Slugs(saved.Source);
        return TextBlockMarkup.Tokenize(description, blocks.Where(b => used.Contains(b.Slug)));
    }

    public async Task<IReadOnlyList<TextBlockSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var blocks = await db.TextBlocks.AsNoTracking().OrderBy(b => b.Name).ToListAsync(ct);
        var uses = await AllUsesAsync(db, blocks, ct);
        return [.. blocks.Select(b =>
        {
            var mine = uses.Where(u => u.Slug == b.Slug).Select(u => u.Use).ToList();
            return new TextBlockSummary(b.Id, b.Name, b.Slug, mine.Count, mine.Count(u => u.Status == TextBlockUseStatus.WillChange),
                mine.Count(u => u.Status == TextBlockUseStatus.ChangedOnEtsy), b.UpdatedAtUtc);
        })];
    }

    public async Task<TextBlock?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TextBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    public async Task<IReadOnlyList<TextBlock>> AllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TextBlocks.AsNoTracking().OrderBy(b => b.Name).ToListAsync(ct);
    }

    /// <summary>
    /// Creates (id null) or updates a block. A blank slug is made from the name. Renaming the slug updates the placeholders
    /// in every description (the text on Etsy doesn't change); new text reaches listings when the block is applied.
    /// </summary>
    public async Task<(int? Id, string? Error)> SaveAsync(int? id, string name, string? slug, string text, string? user, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        text = Normalize(text);
        slug = string.IsNullOrWhiteSpace(slug) ? TextBlockMarkup.MakeSlug(name) : slug.Trim().ToLowerInvariant();
        if (name.Length == 0) return (null, "Give the template a name.");
        if (name.Length > MaxNameLength) return (null, $"The name is {name.Length} characters; keep it to {MaxNameLength}.");
        if (!TextBlockMarkup.IsValidSlug(slug) || slug.Length > MaxSlugLength)
            return (null, $"The placeholder name can only have lowercase letters, numbers and single hyphens (up to {MaxSlugLength}), like \"pla-care\".");
        if (text.Length == 0) return (null, "The template needs some text.");
        if (TextBlockMarkup.Slugs(text).Count > 0) return (null, "A template can't contain another template's placeholder.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.TextBlocks.AnyAsync(b => b.Id != id && b.Name == name, ct)) return (null, $"There's already a template called \"{name}\".");
        if (await db.TextBlocks.AnyAsync(b => b.Id != id && b.Slug == slug, ct)) return (null, $"Another template already uses {TextBlockMarkup.Token(slug)}.");
        var now = time.GetUtcNow().UtcDateTime;
        var block = id is { } existing ? await db.TextBlocks.FirstOrDefaultAsync(b => b.Id == existing, ct) : null;
        if (id is not null && block is null) return (null, "That template no longer exists.");
        if (block is null)
        {
            block = new TextBlock { CreatedAtUtc = now };
            db.TextBlocks.Add(block);
        }
        var oldSlug = block.Slug;
        var changed = block.Text != text;
        (block.Name, block.Slug, block.Text, block.UpdatedAtUtc, block.UpdatedBy) = (name, slug, text, now, user);
        if (id is not null && oldSlug != slug)
            foreach (var row in await db.ListingDescriptions.ToListAsync(ct))
                row.Source = TextBlockMarkup.ReplaceSlug(row.Source, oldSlug, TextBlockMarkup.Token(slug));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "text-block.created" : "text-block.saved", nameof(TextBlock), block.Id.ToString(),
            changed && id is not null ? $"{name}: text changed (not applied to listings yet)" : name, user, ct);
        return (block.Id, null);
    }

    /// <summary>Deletes a block. Listings keep its text: their placeholders become the block's text.</summary>
    public async Task DeleteAsync(int id, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var block = await db.TextBlocks.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (block is null) return;
        foreach (var row in await db.ListingDescriptions.ToListAsync(ct))
            row.Source = TextBlockMarkup.ReplaceSlug(row.Source, block.Slug, block.Text);
        db.TextBlocks.Remove(block);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("text-block.deleted", nameof(TextBlock), id.ToString(), $"{block.Name} (listings keep its text)", user, ct);
    }

    /// <summary>A listing's description with its placeholders, for the editor.</summary>
    public async Task<string> SourceAsync(int listingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listing = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstAsync(l => l.Id == listingId, ct);
        var blocks = await db.TextBlocks.AsNoTracking().ToListAsync(ct);
        var saved = await db.ListingDescriptions.AsNoTracking().FirstOrDefaultAsync(d => d.ListingId == listingId, ct);
        return Source(saved, Description(Model(listing)), blocks);
    }

    /// <summary>
    /// Saves a description written with placeholders: the listing's description becomes its rendered text (an unpushed edit
    /// when it differs), and the source is kept. Returns the placeholders that match no block (they stay as written, and
    /// the push review blocks them).
    /// </summary>
    public async Task<IReadOnlyList<string>> SaveDescriptionAsync(int listingId, string source, string? user, CancellationToken ct = default)
    {
        source = (source ?? "").Replace("\r\n", "\n");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var blocks = await db.TextBlocks.AsNoTracking().ToListAsync(ct);
        var bySlug = blocks.ToDictionary(b => b.Slug);
        var (rendered, unknown) = TextBlockMarkup.Render(source, s => bySlug.GetValueOrDefault(s)?.Text);
        var listing = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstAsync(l => l.Id == listingId, ct);
        if (rendered != Description(Model(listing)))
            await editor.SaveAsync(listingId, new Dictionary<string, JsonNode?> { ["description"] = rendered }, user, ct);
        var row = await db.ListingDescriptions.FirstOrDefaultAsync(d => d.ListingId == listingId, ct);
        if (TextBlockMarkup.Slugs(source).Count == 0)
        {
            if (row is not null) db.ListingDescriptions.Remove(row);
        }
        else
        {
            if (row is null) db.ListingDescriptions.Add(row = new ListingDescription { ListingId = listingId });
            (row.Source, row.Rendered, row.UpdatedAtUtc) = (source, rendered, time.GetUtcNow().UtcDateTime);
        }
        await db.SaveChangesAsync(ct);
        return unknown;
    }

    public async Task<IReadOnlyList<TextBlockUse>> UsesAsync(int blockId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var blocks = await db.TextBlocks.AsNoTracking().ToListAsync(ct);
        var block = blocks.FirstOrDefault(b => b.Id == blockId);
        if (block is null) return [];
        return [.. (await AllUsesAsync(db, blocks, ct)).Where(u => u.Slug == block.Slug).Select(u => u.Use)
            .OrderBy(u => u.Status == TextBlockUseStatus.UpToDate).ThenBy(u => u.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Every (block slug, listing) use, from the saved sources (each checked against its listing's description).</summary>
    private static async Task<List<(string Slug, TextBlockUse Use, string Source)>> AllUsesAsync(AppDbContext db, IReadOnlyCollection<TextBlock> blocks,
        CancellationToken ct)
    {
        var rows = await db.ListingDescriptions.AsNoTracking().Include(d => d.Listing).ThenInclude(l => l.EtsyState)
            .Include(d => d.Listing).ThenInclude(l => l.Shop).ToListAsync(ct);
        var result = new List<(string, TextBlockUse, string)>();
        foreach (var row in rows.Where(r => r.Listing.EtsyState is not null))
        {
            var model = Model(row.Listing);
            var description = Description(model);
            var source = Source(row, description, blocks);
            var after = TextBlockMarkup.Render(source, blocks);
            var pending = !JsonNode.DeepEquals(model.Local("description"), model.Etsy["description"]);
            var status = model.DriftFields.Contains("description") ? TextBlockUseStatus.ChangedOnEtsy
                : after == description ? TextBlockUseStatus.UpToDate : TextBlockUseStatus.WillChange;
            foreach (var slug in TextBlockMarkup.Slugs(source).Where(s => blocks.Any(b => b.Slug == s)))
                result.Add((slug, new TextBlockUse(row.ListingId, row.Listing.Title, row.Listing.Shop.Name, status, description, after, pending), source));
        }
        return result;
    }

    private static ListingEditModel Model(Listing l) =>
        ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);

    /// <summary>The local description (Etsy's with edits not pushed yet), as stored: \n newlines.</summary>
    private static string Description(ListingEditModel model) => ((string?)model.Local("description") ?? "").Replace("\r\n", "\n");

    /// <summary>Listings not using the block whose description already contains its text word for word (pasted by hand before).</summary>
    public async Task<IReadOnlyList<TextBlockCandidate>> CandidatesAsync(int blockId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var blocks = await db.TextBlocks.AsNoTracking().ToListAsync(ct);
        var block = blocks.FirstOrDefault(b => b.Id == blockId);
        if (block is null) return [];
        var using_ = (await AllUsesAsync(db, blocks, ct)).Where(u => u.Slug == block.Slug).Select(u => u.Use.ListingId).ToHashSet();
        var listings = await db.Listings.AsNoTracking().Include(l => l.EtsyState).Include(l => l.Shop)
            .Where(l => l.EtsyState != null && !l.IsMissingOnEtsy).ToListAsync(ct);
        return [.. listings.Where(l => !using_.Contains(l.Id) && Description(Model(l)).Contains(block.Text, StringComparison.Ordinal))
            .OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase).Select(l => new TextBlockCandidate(l.Id, l.Title, l.Shop.Name))];
    }

    /// <summary>
    /// Turns the block's text in these listings' descriptions into its placeholder. The text on Etsy stays the same; from
    /// then on it follows the block. Returns how many listings now use it.
    /// </summary>
    public async Task<int> LinkAsync(int blockId, IEnumerable<int> listingIds, string? user, CancellationToken ct = default)
    {
        TextBlock block;
        List<TextBlock> blocks;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            blocks = await db.TextBlocks.AsNoTracking().ToListAsync(ct);
            block = blocks.First(b => b.Id == blockId);
        }
        var count = 0;
        foreach (var listingId in listingIds.Distinct())
        {
            var source = await SourceAsync(listingId, ct);
            if (!source.Contains(block.Text, StringComparison.Ordinal)) continue;
            await SaveDescriptionAsync(listingId, source.Replace(block.Text, TextBlockMarkup.Token(block.Slug), StringComparison.Ordinal), user, ct);
            count++;
        }
        if (count > 0) await audit.WriteAsync("text-block.linked", nameof(TextBlock), blockId.ToString(), $"{block.Name}: {count} listing(s)", user, ct);
        return count;
    }

    /// <summary>
    /// Stops a listing using the block: its placeholder becomes the block's current text, which the listing can then edit
    /// like any other text (an unpushed edit only if the block changed since it was last applied).
    /// </summary>
    public async Task DetachAsync(int blockId, int listingId, string? user, CancellationToken ct = default)
    {
        var block = await GetAsync(blockId, ct) ?? throw new InvalidOperationException($"Text block {blockId} not found.");
        var source = await SourceAsync(listingId, ct);
        await SaveDescriptionAsync(listingId, TextBlockMarkup.ReplaceSlug(source, block.Slug, block.Text), user, ct);
        await audit.WriteAsync("text-block.detached", nameof(TextBlock), blockId.ToString(), $"listing {listingId} keeps the text", user, ct);
    }

    /// <summary>
    /// Puts the block's current text into every listing that uses it and has an older one, as a draft (nothing is sent to
    /// Etsy). Listings whose description changed on Etsy are skipped. Returns the listings updated.
    /// </summary>
    public async Task<int> ApplyAsync(int blockId, string? user, CancellationToken ct = default)
    {
        List<(string Slug, TextBlockUse Use, string Source)> uses;
        TextBlock block;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var blocks = await db.TextBlocks.AsNoTracking().ToListAsync(ct);
            block = blocks.First(b => b.Id == blockId);
            uses = [.. (await AllUsesAsync(db, blocks, ct)).Where(u => u.Slug == block.Slug && u.Use.Status == TextBlockUseStatus.WillChange)];
        }
        foreach (var (_, use, source) in uses) await SaveDescriptionAsync(use.ListingId, source, user, ct);
        if (uses.Count > 0)
            await audit.WriteAsync("text-block.applied", nameof(TextBlock), blockId.ToString(), $"{block.Name}: {uses.Count} listing draft(s) updated", user, ct);
        return uses.Count;
    }

    /// <summary>The blocks a listing's description uses and where each stands, for the editor.</summary>
    public async Task<IReadOnlyList<(TextBlock Block, TextBlockUseStatus Status)>> ForListingAsync(int listingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var blocks = await db.TextBlocks.AsNoTracking().ToListAsync(ct);
        return [.. (await AllUsesAsync(db, blocks, ct)).Where(u => u.Use.ListingId == listingId)
            .Select(u => (blocks.First(b => b.Slug == u.Slug), u.Use.Status)).OrderBy(x => x.Item1.Name)];
    }

    /// <summary>The review before pushing: each listing using the block with unpushed edits, with the editor's own preview.</summary>
    public async Task<IReadOnlyList<TextBlockPushItem>> ReviewAsync(int blockId, CancellationToken ct = default)
    {
        var items = new List<TextBlockPushItem>();
        foreach (var use in (await UsesAsync(blockId, ct)).Where(u => u.DescriptionPending))
            items.Add(new TextBlockPushItem(use.ListingId, use.Title, use.ShopName, await editor.PreviewAsync(use.ListingId, ct)));
        return items;
    }

    /// <summary>Queues the normal push for every reviewed listing that can go. Returns the outcome per listing.</summary>
    public async Task<IReadOnlyList<(int ListingId, string Title, int? PushId, IReadOnlyList<string> Blockers)>> PushAsync(int blockId,
        string? user, CancellationToken ct = default)
    {
        var results = new List<(int, string, int?, IReadOnlyList<string>)>();
        foreach (var item in await ReviewAsync(blockId, ct))
        {
            var (pushId, blockers) = await editor.RequestPushAsync(item.ListingId, user, ct);
            results.Add((item.ListingId, item.Title, pushId, blockers));
        }
        return results;
    }

    /// <summary>
    /// Once, after upgrading from the first version: gives blocks without a valid slug one made from their name, and turns
    /// the old links (the block found by its exact text) into placeholders wherever the description still has that text.
    /// A listing whose copy was edited by hand keeps its text and isn't linked (it shows up under "listings that already
    /// contain this text" if it has the block's text again).
    /// </summary>
    public async Task ConvertLinksAsync(CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var blocks = await db.TextBlocks.ToListAsync(ct);
            foreach (var b in blocks.Where(b => !TextBlockMarkup.IsValidSlug(b.Slug)))
            {
                var slug = TextBlockMarkup.MakeSlug(b.Name);
                for (var n = 2; blocks.Any(o => o != b && o.Slug == slug); n++) slug = $"{TextBlockMarkup.MakeSlug(b.Name)}-{n}";
                b.Slug = slug;
            }
            await db.SaveChangesAsync(ct);
        }
        List<ListingTextBlock> links;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            links = await db.ListingTextBlocks.AsNoTracking().Include(l => l.TextBlock).ToListAsync(ct);
        foreach (var link in links)
        {
            var source = await SourceAsync(link.ListingId, ct);
            var token = TextBlockMarkup.Token(link.TextBlock.Slug);
            var converted = source.Contains(link.AppliedText, StringComparison.Ordinal) && link.AppliedText.Length > 0
                ? source.Replace(link.AppliedText, token, StringComparison.Ordinal)
                : source.Replace(link.TextBlock.Text, token, StringComparison.Ordinal);
            if (converted == source) continue;
            // The old text becomes the placeholder; if the block changed since, the listing shows "Will change" as before.
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var description = (await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstAsync(l => l.Id == link.ListingId, ct)) is var l
                ? Description(Model(l)) : "";
            var row = await db.ListingDescriptions.FirstOrDefaultAsync(d => d.ListingId == link.ListingId, ct);
            if (row is null) db.ListingDescriptions.Add(row = new ListingDescription { ListingId = link.ListingId });
            (row.Source, row.Rendered, row.UpdatedAtUtc) = (converted, description, time.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
        }
        if (links.Count > 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.ListingTextBlocks.ExecuteDeleteAsync(ct);
            logger.LogInformation("Text blocks: {Count} link(s) from the first version turned into placeholders", links.Count);
        }
    }
}

/// <summary>Runs <see cref="TextBlockService.ConvertLinksAsync"/> once at startup (after migrations).</summary>
public sealed class TextBlockStartup(TextBlockService blocks, ILogger<TextBlockStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try { await blocks.ConvertLinksAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Text blocks: converting the first version's links failed; it is tried again at the next start");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>A line diff for previews: unchanged lines around each change, removed lines and added lines.</summary>
public static class LineDiff
{
    public enum Kind { Same, Removed, Added, Gap }

    public sealed record Line(Kind Kind, string Text);

    /// <summary>The changed lines with <paramref name="context"/> unchanged lines around each change; long unchanged runs become a gap.</summary>
    public static IReadOnlyList<Line> Compare(string before, string after, int context = 1)
    {
        var a = before.Replace("\r\n", "\n").Split('\n');
        var b = after.Replace("\r\n", "\n").Split('\n');
        // Longest common subsequence table (descriptions are short: a few hundred lines at most).
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var all = new List<Line>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { all.Add(new(Kind.Same, a[x])); x++; y++; }
            else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1])) all.Add(new(Kind.Removed, a[x++])); // removed lines first
            else all.Add(new(Kind.Added, b[y++]));
        }
        var keep = new bool[all.Count];
        for (var i = 0; i < all.Count; i++)
            if (all[i].Kind != Kind.Same)
                for (var k = Math.Max(0, i - context); k <= Math.Min(all.Count - 1, i + context); k++) keep[k] = true;
        var result = new List<Line>();
        for (var i = 0; i < all.Count; i++)
        {
            if (keep[i]) result.Add(all[i]);
            else if (result.Count == 0 || result[^1].Kind != Kind.Gap) result.Add(new(Kind.Gap, ""));
        }
        if (result.Count > 0 && result[^1].Kind == Kind.Gap) result.RemoveAt(result.Count - 1);
        return result;
    }
}

/// <summary>Phase 4: templates (text templates, variation templates, photo templates).</summary>
public sealed class TemplatesModule : IModule
{
    public string Name => "Templates";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<TextBlockService>();
        services.AddHostedService<TextBlockStartup>();
        services.AddSingleton<VariationListService>();
        services.AddSingleton<PhotoTemplateService>();
        services.AddScoped<EtsyShopManager.Core.Events.IDomainEventHandler<EtsyShopManager.Core.Events.ListingPushed>, PhotoTemplatesOnListingPushed>();
    }

    public IEnumerable<NavItem> NavItems =>
    [
        // One entry; the Templates page has a tab each for text, variation and photo templates.
        new("Templates", "templates", Icons.Material.Filled.DashboardCustomize, Order: 25, Roles: "Admin,Editor"),
    ];
}
