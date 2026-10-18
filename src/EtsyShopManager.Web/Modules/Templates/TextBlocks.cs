using System.Text.Json.Nodes;
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
    /// <summary>The description has an older text of the block; applying replaces it.</summary>
    WillChange,
    /// <summary>The block's text (as the app last put it there) isn't in the description any more; left alone.</summary>
    EditedByHand,
    /// <summary>The description changed on Etsy since the last sync; resolve that in the editor first.</summary>
    ChangedOnEtsy,
}

/// <summary>A listing using a block: where it stands, and its description before and after applying the block.</summary>
public sealed record TextBlockUse(int ListingId, string Title, string ShopName, TextBlockUseStatus Status, string Before, string After,
    bool DescriptionPending);

public sealed record TextBlockSummary(int Id, string Name, int Listings, int WillChange, int NeedAttention, DateTime UpdatedAtUtc);

/// <summary>A listing that isn't linked yet but whose description already contains the block's text.</summary>
public sealed record TextBlockCandidate(int ListingId, string Title, string ShopName);

/// <summary>What pushing a block's listings would do, per listing (the editor's own review).</summary>
public sealed record TextBlockPushItem(int ListingId, string Title, string ShopName, PushPreview Preview);

/// <summary>
/// Reusable description text (Phase 4, text templates). A listing's description stays plain text on Etsy; the app keeps
/// which listings use a block and the exact text it last put there (<see cref="ListingTextBlock.AppliedText"/>), and finds
/// the block in the description by that text. Editing a block changes nothing on its own: applying it writes the new
/// descriptions as drafts, and pushing goes through <see cref="ListingEditor"/>'s reviewed push, listing by listing.
/// </summary>
public sealed class TextBlockService(IDbContextFactory<AppDbContext> dbFactory, ListingEditor editor, IAuditLog audit, TimeProvider time)
{
    public const int MaxNameLength = 80;

    /// <summary>Block text as stored and compared: \n newlines, no trailing spaces on lines, no blank lines around it.</summary>
    public static string Normalize(string? text) =>
        string.Join("\n", (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd())).Trim('\n', ' ');

    public async Task<IReadOnlyList<TextBlockSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var blocks = await db.TextBlocks.AsNoTracking().OrderBy(b => b.Name).ToListAsync(ct);
        var result = new List<TextBlockSummary>();
        foreach (var b in blocks)
        {
            var uses = await UsesAsync(db, b, ct);
            result.Add(new TextBlockSummary(b.Id, b.Name, uses.Count, uses.Count(u => u.Status == TextBlockUseStatus.WillChange),
                uses.Count(u => u.Status is TextBlockUseStatus.EditedByHand or TextBlockUseStatus.ChangedOnEtsy), b.UpdatedAtUtc));
        }
        return result;
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

    /// <summary>Creates (id null) or updates a block. Only the app's copy changes; listings change when the block is applied.</summary>
    public async Task<(int? Id, string? Error)> SaveAsync(int? id, string name, string text, string? user, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        text = Normalize(text);
        if (name.Length == 0) return (null, "Give the block a name.");
        if (name.Length > MaxNameLength) return (null, $"The name is {name.Length} characters; keep it to {MaxNameLength}.");
        if (text.Length == 0) return (null, "The block needs some text.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.TextBlocks.AnyAsync(b => b.Id != id && b.Name == name, ct)) return (null, $"There's already a block called \"{name}\".");
        var now = time.GetUtcNow().UtcDateTime;
        var block = id is { } existing ? await db.TextBlocks.FirstOrDefaultAsync(b => b.Id == existing, ct) : null;
        if (id is not null && block is null) return (null, "That block no longer exists.");
        if (block is null)
        {
            block = new TextBlock { CreatedAtUtc = now };
            db.TextBlocks.Add(block);
        }
        var changed = block.Text != text;
        (block.Name, block.Text, block.UpdatedAtUtc, block.UpdatedBy) = (name, text, now, user);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "text-block.created" : "text-block.saved", nameof(TextBlock), block.Id.ToString(),
            changed && id is not null ? $"{name}: text changed (not applied to listings yet)" : name, user, ct);
        return (block.Id, null);
    }

    /// <summary>Deletes a block. Listings keep their text; they're just no longer linked.</summary>
    public async Task DeleteAsync(int id, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var block = await db.TextBlocks.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (block is null) return;
        db.TextBlocks.Remove(block);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("text-block.deleted", nameof(TextBlock), id.ToString(), $"{block.Name} (listings keep their text)", user, ct);
    }

    public async Task<IReadOnlyList<TextBlockUse>> UsesAsync(int blockId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var block = await db.TextBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == blockId, ct);
        return block is null ? [] : await UsesAsync(db, block, ct);
    }

    private static async Task<List<TextBlockUse>> UsesAsync(AppDbContext db, TextBlock block, CancellationToken ct)
    {
        var links = await db.ListingTextBlocks.AsNoTracking().Where(l => l.TextBlockId == block.Id)
            .Include(l => l.Listing).ThenInclude(l => l.EtsyState).Include(l => l.Listing).ThenInclude(l => l.Shop)
            .ToListAsync(ct);
        return [.. links.Where(l => l.Listing.EtsyState is not null).Select(l => Use(block, l))
            .OrderBy(u => u.Status == TextBlockUseStatus.UpToDate).ThenBy(u => u.Title, StringComparer.OrdinalIgnoreCase)];
    }

    private static TextBlockUse Use(TextBlock block, ListingTextBlock link)
    {
        var listing = link.Listing;
        var model = Model(listing);
        var description = Description(model);
        var pending = !JsonNode.DeepEquals(model.Local("description"), model.Etsy["description"]);
        var status = model.DriftFields.Contains("description") ? TextBlockUseStatus.ChangedOnEtsy
            : description.Contains(link.AppliedText, StringComparison.Ordinal)
                ? link.AppliedText == block.Text ? TextBlockUseStatus.UpToDate : TextBlockUseStatus.WillChange
            : description.Contains(block.Text, StringComparison.Ordinal) ? TextBlockUseStatus.UpToDate
            : TextBlockUseStatus.EditedByHand;
        var after = status == TextBlockUseStatus.WillChange ? description.Replace(link.AppliedText, block.Text, StringComparison.Ordinal) : description;
        return new TextBlockUse(listing.Id, listing.Title, listing.Shop.Name, status, description, after, pending);
    }

    private static ListingEditModel Model(Listing l) =>
        ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);

    /// <summary>The local description (Etsy's with edits not pushed yet), as stored: \n newlines.</summary>
    private static string Description(ListingEditModel model) => ((string?)model.Local("description") ?? "").Replace("\r\n", "\n");

    /// <summary>Listings not linked yet whose description already contains the block's text (e.g. pasted by hand before).</summary>
    public async Task<IReadOnlyList<TextBlockCandidate>> CandidatesAsync(int blockId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var block = await db.TextBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == blockId, ct);
        if (block is null) return [];
        var linked = await db.ListingTextBlocks.Where(l => l.TextBlockId == blockId).Select(l => l.ListingId).ToListAsync(ct);
        var listings = await db.Listings.AsNoTracking().Include(l => l.EtsyState).Include(l => l.Shop)
            .Where(l => !linked.Contains(l.Id) && l.EtsyState != null && !l.IsMissingOnEtsy).ToListAsync(ct);
        return [.. listings.Where(l => Description(Model(l)).Contains(block.Text, StringComparison.Ordinal))
            .OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase).Select(l => new TextBlockCandidate(l.Id, l.Title, l.Shop.Name))];
    }

    /// <summary>
    /// Links listings whose description contains the block's current text (also "relinks" one edited by hand once its
    /// description has the block's text again). Returns how many were linked.
    /// </summary>
    public async Task<int> LinkAsync(int blockId, IEnumerable<int> listingIds, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var block = await db.TextBlocks.FirstAsync(b => b.Id == blockId, ct);
        var ids = listingIds.Distinct().ToList();
        var listings = await db.Listings.Include(l => l.EtsyState).Where(l => ids.Contains(l.Id) && l.EtsyState != null).ToListAsync(ct);
        var links = await db.ListingTextBlocks.Where(l => l.TextBlockId == blockId && ids.Contains(l.ListingId)).ToDictionaryAsync(l => l.ListingId, ct);
        var count = 0;
        foreach (var listing in listings.Where(l => Description(Model(l)).Contains(block.Text, StringComparison.Ordinal)))
        {
            if (!links.TryGetValue(listing.Id, out var link))
                db.ListingTextBlocks.Add(link = new ListingTextBlock { ListingId = listing.Id, TextBlockId = blockId });
            link.AppliedText = block.Text;
            link.LinkedAtUtc = time.GetUtcNow().UtcDateTime;
            count++;
        }
        await db.SaveChangesAsync(ct);
        if (count > 0) await audit.WriteAsync("text-block.linked", nameof(TextBlock), blockId.ToString(), $"{block.Name}: {count} listing(s)", user, ct);
        return count;
    }

    public async Task UnlinkAsync(int blockId, int listingId, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ListingTextBlocks.Where(l => l.TextBlockId == blockId && l.ListingId == listingId).ExecuteDeleteAsync(ct);
        await audit.WriteAsync("text-block.unlinked", nameof(TextBlock), blockId.ToString(), $"listing {listingId} (keeps its text)", user, ct);
    }

    /// <summary>
    /// Puts the block's current text into every listing that has an older one, as a draft (nothing is sent to Etsy).
    /// Listings edited by hand or changed on Etsy are skipped. Returns the listings updated.
    /// </summary>
    public async Task<int> ApplyAsync(int blockId, string? user, CancellationToken ct = default)
    {
        TextBlock block;
        List<TextBlockUse> uses;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            block = await db.TextBlocks.AsNoTracking().FirstAsync(b => b.Id == blockId, ct);
            uses = await UsesAsync(db, block, ct);
        }
        var applied = 0;
        foreach (var use in uses.Where(u => u.Status == TextBlockUseStatus.WillChange))
        {
            await editor.SaveAsync(use.ListingId, new Dictionary<string, JsonNode?> { ["description"] = use.After }, user, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.ListingTextBlocks.Where(l => l.TextBlockId == blockId && l.ListingId == use.ListingId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.AppliedText, block.Text), ct);
            applied++;
        }
        if (applied > 0)
            await audit.WriteAsync("text-block.applied", nameof(TextBlock), blockId.ToString(), $"{block.Name}: {applied} listing draft(s) updated", user, ct);
        return applied;
    }

    /// <summary>Adds the block to the end of a listing's description (as a draft) and links it.</summary>
    public async Task InsertAsync(int listingId, int blockId, string? user, CancellationToken ct = default)
    {
        TextBlock block;
        string description;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            block = await db.TextBlocks.AsNoTracking().FirstAsync(b => b.Id == blockId, ct);
            var listing = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstAsync(l => l.Id == listingId, ct);
            description = Description(Model(listing)).TrimEnd();
        }
        if (!description.Contains(block.Text, StringComparison.Ordinal))
        {
            description = description.Length == 0 ? block.Text : $"{description}\n\n{block.Text}";
            await editor.SaveAsync(listingId, new Dictionary<string, JsonNode?> { ["description"] = description }, user, ct);
        }
        await LinkAsync(blockId, [listingId], user, ct);
    }

    /// <summary>The blocks a listing uses and where each stands, for the editor.</summary>
    public async Task<IReadOnlyList<(TextBlock Block, TextBlockUseStatus Status)>> ForListingAsync(int listingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var links = await db.ListingTextBlocks.AsNoTracking().Where(l => l.ListingId == listingId).Include(l => l.TextBlock)
            .Include(l => l.Listing).ThenInclude(l => l.EtsyState).Include(l => l.Listing).ThenInclude(l => l.Shop).ToListAsync(ct);
        return [.. links.Where(l => l.Listing.EtsyState is not null).Select(l => (l.TextBlock, Use(l.TextBlock, l).Status))
            .OrderBy(x => x.TextBlock.Name)];
    }

    /// <summary>The review before pushing: each linked listing with unpushed edits, with the editor's own preview.</summary>
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

/// <summary>Phase 4: templates (text blocks, shared variation lists, photo templates).</summary>
public sealed class TemplatesModule : IModule
{
    public string Name => "Templates";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<TextBlockService>();
        services.AddSingleton<VariationListService>();
        services.AddSingleton<PhotoTemplateService>();
        services.AddScoped<EtsyShopManager.Core.Events.IDomainEventHandler<EtsyShopManager.Core.Events.ListingPushed>, PhotoTemplatesOnListingPushed>();
    }

    public IEnumerable<NavItem> NavItems =>
    [
        new("Text blocks", "text-blocks", Icons.Material.Filled.ShortText, Order: 25, Roles: "Admin,Editor"),
        new("Variation lists", "variation-lists", Icons.Material.Filled.Palette, Order: 26, Roles: "Admin,Editor"),
        new("Photo templates", "photo-templates", Icons.Material.Filled.Collections, Order: 27, Roles: "Admin,Editor"),
    ];
}
