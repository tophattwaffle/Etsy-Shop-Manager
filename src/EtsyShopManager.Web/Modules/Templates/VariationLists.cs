using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Templates;

public enum VariationListUseStatus
{
    UpToDate,
    /// <summary>The list changed since it was last applied; applying changes the listing's variation.</summary>
    HasChanges,
    /// <summary>The listing no longer has the linked variation.</summary>
    VariationMissing,
    /// <summary>The listing's variations changed on Etsy since the last sync; resolve that in the editor first.</summary>
    ChangedOnEtsy,
}

/// <summary>What applying a list would do to one listing's local inventory.</summary>
/// <param name="Changes">The changes in words ("Adds Galaxy Purple", "Makes Neon Green unavailable").</param>
/// <param name="Notes">Options left alone on this listing and why.</param>
public sealed record VariationListPlan(JsonObject Inventory, List<VariationListOption> Applied, IReadOnlyList<string> Changes,
    IReadOnlyList<string> Notes);

public sealed record VariationListUse(int ListingId, string Title, string ShopName, long PropertyId, string VariationName,
    VariationListUseStatus Status, IReadOnlyList<string> Changes, IReadOnlyList<string> Notes, bool InventoryPending);

public sealed record VariationListSummary(int Id, string Name, int Options, int Enabled, int Listings, int WithChanges, int NeedAttention,
    DateTime UpdatedAtUtc);

/// <summary>What adding a list to a listing as a new variation does.</summary>
/// <param name="Combinations">The listing's combinations afterwards (each existing one times each option).</param>
/// <param name="Applied">The options the link starts with.</param>
public sealed record VariationListAddPlan(JsonObject Inventory, long PropertyId, string VariationName, IReadOnlyList<string> Values,
    int Combinations, List<VariationListOption> Applied);

/// <summary>A variation of a listing and the list it follows.</summary>
public sealed record VariationListLink(int ListId, string ListName, long PropertyId);

/// <summary>What linking a variation to a list means: how many of its values are in the list and what applying would change.</summary>
public sealed record VariationListLinkPreview(int InList, int Values, string? Warning, IReadOnlyList<string> Changes, IReadOnlyList<string> Notes);

/// <summary>A listing the list can be added to as a new variation, with the variations it already has.</summary>
public sealed record VariationListAddable(int ListingId, string Title, string ShopName, IReadOnlyList<string> Variations);

/// <summary>A listing variation not linked yet whose values are mostly in the list.</summary>
public sealed record VariationListCandidate(int ListingId, string Title, string ShopName, long PropertyId, string VariationName,
    int InList, int Values);

/// <summary>
/// Shared variation lists (Phase 4, variation templates). A list is a named set of options; a linked listing's variation
/// follows it: new options are added (combinations available, price following what it varies by, stock from the listing's
/// current stock when pushed), options switched off make their combinations unavailable (never deleted), switched back on
/// make them available, and renames carry over. Values a listing has of its own stay as they are. Changes are worked out
/// against what was last applied to each listing (<see cref="ListingVariationList.AppliedOptions"/>), become drafts on
/// "apply", and reach Etsy only through each listing's normal reviewed push.
/// </summary>
public sealed class VariationListService(IDbContextFactory<AppDbContext> dbFactory, ListingEditor editor, IAuditLog audit, TimeProvider time)
{
    public const int MaxNameLength = 80;
    /// <summary>A variation is offered for linking when at least this many of its values are in the list (and at least half).</summary>
    public const int MinOverlap = 2;
    /// <summary>Etsy's limit on a variation's options (a listing with one variation has at most 70 products).</summary>
    public const int MaxOptions = 70;

    /// <summary>
    /// The plan for one listing (pure). Options are taken in the list's order; new ones go at the end of the listing's
    /// values (Etsy shows new values last).
    /// </summary>
    public static VariationListPlan Plan(VariationList list, ListingVariationList link, JsonNode localInventory)
    {
        var inventory = (JsonObject)localInventory.DeepClone();
        var changes = new List<string>();
        var notes = new List<string>();
        var applied = link.AppliedOptions.ToDictionary(o => o.Id);
        var label = VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == link.PropertyId)?.Name ?? "the variation";
        string? Has(string name) => VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == link.PropertyId)?
            .Values.FirstOrDefault(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase));

        foreach (var option in list.Options)
        {
            if (!applied.TryGetValue(option.Id, out var before))
            {
                // New to this listing: add it when it's on and the listing doesn't have it yet (it may have it of its own).
                if (!option.Enabled || Has(option.Name) is not null) continue;
                try
                {
                    inventory = VariationEditor.AddValue(inventory, link.PropertyId, option.Name);
                    changes.Add($"Adds {option.Name}");
                }
                catch (ArgumentException ex) { notes.Add(ex.Message); }
                continue;
            }
            var current = Has(before.Name);
            if (current is null)
            {
                notes.Add($"{before.Name} was removed from this listing by hand, so the list leaves it out.");
                continue;
            }
            if (option.Name != current)
            {
                try
                {
                    inventory = VariationEditor.RenameValue(inventory, link.PropertyId, current, option.Name);
                    changes.Add($"Renames {current} to {option.Name}");
                    current = option.Name;
                }
                catch (ArgumentException ex) { notes.Add(ex.Message); }
            }
            if (option.Enabled != before.Enabled && VariationEditor.IsValueAvailable(inventory, link.PropertyId, current) != option.Enabled)
            {
                inventory = VariationEditor.SetValueAvailable(inventory, link.PropertyId, current, option.Enabled);
                changes.Add(option.Enabled ? $"Makes {current} available again" : $"Makes {current} unavailable");
            }
        }

        // What the listing now reflects: each list option it has, under the name it has it.
        var newApplied = new List<VariationListOption>();
        foreach (var option in list.Options)
        {
            var name = Has(option.Name) ?? (applied.TryGetValue(option.Id, out var before) ? Has(before.Name) : null);
            if (name is null) continue;
            newApplied.Add(new VariationListOption(option.Id, name,
                applied.ContainsKey(option.Id) || changes.Contains($"Adds {option.Name}")
                    ? option.Enabled
                    : VariationEditor.IsValueAvailable(inventory, link.PropertyId, name)));
        }
        if (changes.Count > 0) changes.Insert(0, $"{label}:");
        return new VariationListPlan(inventory, newApplied, changes, notes);
    }

    /// <summary>The options a list starts a link with: those the listing has, as they are on the listing (available or not).</summary>
    public static List<VariationListOption> InitialApplied(VariationList list, long propertyId, JsonNode inventory)
    {
        var values = VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == propertyId)?.Values ?? [];
        return [.. list.Options.Select(o => (o, Name: values.FirstOrDefault(v => string.Equals(v, o.Name, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Name is not null)
            .Select(x => new VariationListOption(x.o.Id, x.Name!, VariationEditor.IsValueAvailable(inventory, propertyId, x.Name!)))];
    }

    public async Task<IReadOnlyList<VariationListSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lists = await db.VariationLists.AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
        var result = new List<VariationListSummary>();
        foreach (var list in lists)
        {
            var uses = await UsesAsync(db, list, ct);
            result.Add(new VariationListSummary(list.Id, list.Name, list.Options.Count, list.Options.Count(o => o.Enabled), uses.Count,
                uses.Count(u => u.Status == VariationListUseStatus.HasChanges),
                uses.Count(u => u.Status is VariationListUseStatus.VariationMissing or VariationListUseStatus.ChangedOnEtsy), list.UpdatedAtUtc));
        }
        return result;
    }

    public async Task<VariationList?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.VariationLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
    }

    /// <summary>
    /// Creates or updates a list. Options with id 0 are new and get an id; the others keep theirs (so a rename is a rename).
    /// Listings change only when the list is applied.
    /// </summary>
    public async Task<(int? Id, string? Error)> SaveAsync(int? id, string name, IEnumerable<VariationListOption> options, string? user,
        CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        var opts = options.Select(o => o with { Name = (o.Name ?? "").Trim() }).ToList();
        if (name.Length == 0) return (null, "Give the list a name.");
        if (name.Length > MaxNameLength) return (null, $"The name is {name.Length} characters; keep it to {MaxNameLength}.");
        if (opts.Count == 0) return (null, "The list needs at least one option.");
        if (opts.Any(o => o.Name.Length == 0)) return (null, "Every option needs a name.");
        if (opts.GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
            return (null, $"\"{dup.Key}\" is in the list twice.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.VariationLists.AnyAsync(l => l.Id != id && l.Name == name, ct)) return (null, $"There's already a list called \"{name}\".");
        var now = time.GetUtcNow().UtcDateTime;
        var list = id is { } existing ? await db.VariationLists.FirstOrDefaultAsync(l => l.Id == existing, ct) : null;
        if (id is not null && list is null) return (null, "That list no longer exists.");
        if (list is null)
        {
            list = new VariationList { CreatedAtUtc = now };
            db.VariationLists.Add(list);
        }
        var next = list.NextOptionId;
        list.Options = [.. opts.Select(o => o.Id > 0 ? o : o with { Id = next++ })];
        (list.NextOptionId, list.Name, list.UpdatedAtUtc, list.UpdatedBy) = (next, name, now, user);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "variation-list.created" : "variation-list.saved", nameof(VariationList), list.Id.ToString(),
            $"{name}: {list.Options.Count} options ({list.Options.Count(o => o.Enabled)} on)", user, ct);
        return (list.Id, null);
    }

    /// <summary>Deletes a list. Listings keep their variations as they are.</summary>
    public async Task DeleteAsync(int id, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.VariationLists.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (list is null) return;
        db.VariationLists.Remove(list);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("variation-list.deleted", nameof(VariationList), id.ToString(), $"{list.Name} (listings keep their variations)", user, ct);
    }

    public async Task<IReadOnlyList<VariationListUse>> UsesAsync(int listId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.VariationLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct);
        return list is null ? [] : await UsesAsync(db, list, ct);
    }

    private static async Task<List<VariationListUse>> UsesAsync(AppDbContext db, VariationList list, CancellationToken ct)
    {
        var links = await db.ListingVariationLists.AsNoTracking().Where(l => l.VariationListId == list.Id)
            .Include(l => l.Listing).ThenInclude(l => l.EtsyState).Include(l => l.Listing).ThenInclude(l => l.Shop).ToListAsync(ct);
        return [.. links.Where(l => l.Listing.EtsyState is not null).Select(l => Use(list, l))
            .OrderBy(u => u.Status == VariationListUseStatus.UpToDate).ThenBy(u => u.Title, StringComparer.OrdinalIgnoreCase)];
    }

    private static VariationListUse Use(VariationList list, ListingVariationList link)
    {
        var listing = link.Listing;
        var model = ListingEditModel.Parse(listing.EtsyState!.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
        var inventory = model.Local(ListingFields.Inventory) ?? new JsonObject();
        var pending = !JsonNode.DeepEquals(inventory, model.Etsy[ListingFields.Inventory]);
        var variation = VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == link.PropertyId);
        if (model.DriftFields.Contains(ListingFields.Inventory))
            return new(listing.Id, listing.Title, listing.Shop.Name, link.PropertyId, variation?.Name ?? "", VariationListUseStatus.ChangedOnEtsy, [], [], pending);
        if (variation is null)
            return new(listing.Id, listing.Title, listing.Shop.Name, link.PropertyId, "", VariationListUseStatus.VariationMissing, [],
                ["The linked variation isn't on this listing any more. Unlink it, or add the variation back in the editor."], pending);
        var plan = Plan(list, link, inventory);
        return new(listing.Id, listing.Title, listing.Shop.Name, link.PropertyId, variation.Name ?? "",
            plan.Changes.Count > 0 ? VariationListUseStatus.HasChanges : VariationListUseStatus.UpToDate, plan.Changes, plan.Notes, pending);
    }

    /// <summary>Listing variations not linked to this list whose values are mostly in it.</summary>
    public async Task<IReadOnlyList<VariationListCandidate>> CandidatesAsync(int listId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.VariationLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct);
        if (list is null) return [];
        var linked = (await db.ListingVariationLists.Select(l => new { l.ListingId, l.PropertyId }).ToListAsync(ct))
            .Select(l => (l.ListingId, l.PropertyId)).ToHashSet();
        var names = list.Options.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listings = await db.Listings.AsNoTracking().Include(l => l.EtsyState).Include(l => l.Shop)
            .Where(l => l.EtsyState != null && !l.IsMissingOnEtsy).ToListAsync(ct);
        var result = new List<VariationListCandidate>();
        foreach (var l in listings)
        {
            var model = ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);
            if (model.Local(ListingFields.Inventory) is not { } inventory) continue;
            foreach (var v in VariationEditor.Variations(inventory).Where(v => !linked.Contains((l.Id, v.PropertyId))))
            {
                var inList = v.Values.Count(names.Contains);
                if (inList >= MinOverlap && inList * 2 >= v.Values.Count)
                    result.Add(new VariationListCandidate(l.Id, l.Title, l.Shop.Name, v.PropertyId, v.Name ?? $"Variation {v.PropertyId}", inList, v.Values.Count));
            }
        }
        return [.. result.OrderByDescending(c => (double)c.InList / c.Values).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Links listing variations to the list, starting from what each listing has now. Returns how many were linked.</summary>
    public async Task<int> LinkAsync(int listId, IEnumerable<(int ListingId, long PropertyId)> variations, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.VariationLists.AsNoTracking().FirstAsync(l => l.Id == listId, ct);
        var count = 0;
        foreach (var (listingId, propertyId) in variations.Distinct())
        {
            var listing = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstOrDefaultAsync(l => l.Id == listingId, ct);
            if (listing?.EtsyState is null) continue;
            var model = ListingEditModel.Parse(listing.EtsyState.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
            if (model.Local(ListingFields.Inventory) is not { } inventory
                || VariationEditor.Variations(inventory).All(v => v.PropertyId != propertyId)) continue;
            if (await db.ListingVariationLists.AnyAsync(l => l.ListingId == listingId && l.PropertyId == propertyId, ct)) continue;
            db.ListingVariationLists.Add(new ListingVariationList
            {
                ListingId = listingId, VariationListId = listId, PropertyId = propertyId,
                AppliedOptions = InitialApplied(list, propertyId, inventory), LinkedAtUtc = time.GetUtcNow().UtcDateTime,
            });
            count++;
        }
        await db.SaveChangesAsync(ct);
        if (count > 0) await audit.WriteAsync("variation-list.linked", nameof(VariationList), listId.ToString(), $"{list.Name}: {count} listing(s)", user, ct);
        return count;
    }

    /// <summary>
    /// What adding the list as a new variation does to a listing's local inventory (pure): a custom variation with the
    /// list's switched-on options, every existing combination taking each option (price, processing and availability copied,
    /// so the price doesn't vary by the new variation; stock is set when pushed). Throws <see cref="ArgumentException"/>
    /// with the reason when it can't be added.
    /// </summary>
    public static VariationListAddPlan PlanAdd(VariationList list, string variationName, JsonNode localInventory)
    {
        var variations = VariationEditor.Variations(localInventory);
        if (variations.Count >= VariationEditor.MaxVariations)
            throw new ArgumentException($"This listing already has {VariationEditor.MaxVariations} variations " +
                $"({string.Join(" and ", variations.Select(v => v.Name))}); remove one in the editor first.");
        var options = list.Options.Where(o => o.Enabled).ToList();
        if (options.Count == 0) throw new ArgumentException("Every option in the list is switched off.");
        if (options.Count > MaxOptions) throw new ArgumentException($"The list has {options.Count} options switched on; Etsy allows at most {MaxOptions} in a variation.");
        var inventory = VariationEditor.AddVariation(localInventory, variationName, options.Select(o => o.Name));
        var added = VariationEditor.Variations(inventory).Single(v => variations.All(o => o.PropertyId != v.PropertyId));
        var combinations = inventory["products"]?.AsArray().Count ?? 0;
        return new VariationListAddPlan(inventory, added.PropertyId, added.Name ?? variationName, [.. options.Select(o => o.Name)], combinations, options);
    }

    /// <summary>The variation name to suggest when adding the list: the name most of its linked listings use, else the list's name.</summary>
    public async Task<string> SuggestedVariationNameAsync(int listId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.VariationLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct);
        if (list is null) return "";
        var name = (await UsesAsync(db, list, ct)).Select(u => u.VariationName).Where(n => n.Length > 0)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.First()).FirstOrDefault();
        return name ?? list.Name;
    }

    /// <summary>Listings the list can be added to as a new variation: not linked to it yet, and with room for another variation.</summary>
    public async Task<IReadOnlyList<VariationListAddable>> AddableAsync(int listId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var linked = (await db.ListingVariationLists.Where(l => l.VariationListId == listId).Select(l => l.ListingId).ToListAsync(ct)).ToHashSet();
        var listings = await db.Listings.AsNoTracking().Include(l => l.EtsyState).Include(l => l.Shop)
            .Where(l => l.EtsyState != null && !l.IsMissingOnEtsy).ToListAsync(ct);
        var result = new List<VariationListAddable>();
        foreach (var l in listings.Where(l => !linked.Contains(l.Id)))
        {
            var model = ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);
            if (model.Local(ListingFields.Inventory) is not { } inventory) continue;
            var variations = VariationEditor.Variations(inventory);
            if (variations.Count < VariationEditor.MaxVariations)
                result.Add(new VariationListAddable(l.Id, l.Title, l.Shop.Name, [.. variations.Select(v => v.Name ?? "")]));
        }
        return [.. result.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>What adding the list to a listing would do, or why it can't (nothing is saved).</summary>
    public async Task<(VariationListAddPlan? Plan, string? Error)> PreviewAddAsync(int listId, int listingId, string variationName,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (list, inventory, error) = await AddSourceAsync(db, listId, listingId, ct);
        if (error is not null) return (null, error);
        try { return (PlanAdd(list!, variationName, inventory!), null); }
        catch (ArgumentException ex) { return (null, ex.Message); }
    }

    /// <summary>
    /// Adds the list to a listing as a new variation, as a draft (nothing is sent to Etsy), and links it so later changes
    /// to the list follow. Pushed through the listing's normal reviewed push.
    /// </summary>
    public async Task<(VariationListAddPlan? Plan, string? Error)> AddToListingAsync(int listId, int listingId, string variationName, string? user,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (list, inventory, error) = await AddSourceAsync(db, listId, listingId, ct);
        if (error is not null) return (null, error);
        VariationListAddPlan plan;
        try { plan = PlanAdd(list!, variationName, inventory!); }
        catch (ArgumentException ex) { return (null, ex.Message); }
        await editor.SaveAsync(listingId, new Dictionary<string, JsonNode?> { [ListingFields.Inventory] = plan.Inventory }, user, ct);
        db.ListingVariationLists.Add(new ListingVariationList
        {
            ListingId = listingId, VariationListId = listId, PropertyId = plan.PropertyId, AppliedOptions = plan.Applied,
            LinkedAtUtc = time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("variation-list.added", nameof(VariationList), listId.ToString(),
            $"{list!.Name}: added as {plan.VariationName} to listing {listingId} ({plan.Combinations} combinations, draft)", user, ct);
        return (plan, null);
    }

    private static async Task<(VariationList? List, JsonNode? Inventory, string? Error)> AddSourceAsync(AppDbContext db, int listId, int listingId,
        CancellationToken ct)
    {
        var list = await db.VariationLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct);
        if (list is null) return (null, null, "That list no longer exists.");
        var listing = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing?.EtsyState is null) return (null, null, "That listing hasn't been imported.");
        if (await db.ListingVariationLists.AnyAsync(l => l.ListingId == listingId && l.VariationListId == listId, ct))
            return (null, null, $"{listing.Title} already uses this list.");
        var model = ListingEditModel.Parse(listing.EtsyState.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
        if (model.DriftFields.Contains(ListingFields.Inventory))
            return (null, null, $"The variations of {listing.Title} changed on Etsy; resolve that in the editor first.");
        return model.Local(ListingFields.Inventory) is { } inventory
            ? (list, inventory, null)
            : (null, null, $"{listing.Title} has no inventory imported; refresh it from Etsy first.");
    }

    /// <summary>The lists a listing's variations follow.</summary>
    public async Task<IReadOnlyList<VariationListLink>> ListingLinksAsync(int listingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ListingVariationLists.AsNoTracking().Where(l => l.ListingId == listingId)
            .Select(l => new VariationListLink(l.VariationListId, l.VariationList.Name, l.PropertyId)).ToListAsync(ct);
    }

    /// <summary>
    /// What linking one of a listing's variations to a list means (nothing is saved): how many of its values are in the
    /// list, a warning when few are, and what applying the list would then change. Any list can be chosen here (no match
    /// threshold, unlike <see cref="CandidatesAsync"/>).
    /// </summary>
    public async Task<(VariationListLinkPreview? Preview, string? Error)> PreviewLinkAsync(int listId, int listingId, long propertyId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (list, inventory, error) = await LinkSourceAsync(db, listId, listingId, propertyId, ct);
        if (error is not null) return (null, error);
        var variation = VariationEditor.Variations(inventory!).Single(v => v.PropertyId == propertyId);
        var names = list!.Options.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inList = variation.Values.Count(names.Contains);
        var link = new ListingVariationList { PropertyId = propertyId, AppliedOptions = InitialApplied(list, propertyId, inventory!) };
        var plan = Plan(list, link, inventory!);
        string? warning = inList >= MinOverlap && inList * 2 >= variation.Values.Count ? null
            : inList == 0 ? $"None of {variation.Name}'s values are in {list.Name}; applying it adds all of the list's options next to them."
            : $"Only {inList} of {variation.Values.Count} values are in {list.Name}; the others stay as the listing's own.";
        return (new VariationListLinkPreview(inList, variation.Values.Count, warning, plan.Changes, plan.Notes), null);
    }

    /// <summary>Links one variation of a listing to a list, starting from what the listing has. Changes come with the next apply.</summary>
    public async Task<string?> LinkVariationAsync(int listId, int listingId, long propertyId, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (list, inventory, error) = await LinkSourceAsync(db, listId, listingId, propertyId, ct);
        if (error is not null) return error;
        db.ListingVariationLists.Add(new ListingVariationList
        {
            ListingId = listingId, VariationListId = listId, PropertyId = propertyId,
            AppliedOptions = InitialApplied(list!, propertyId, inventory!), LinkedAtUtc = time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("variation-list.linked", nameof(VariationList), listId.ToString(), $"{list!.Name}: listing {listingId}, from its editor", user, ct);
        return null;
    }

    private static async Task<(VariationList? List, JsonNode? Inventory, string? Error)> LinkSourceAsync(AppDbContext db, int listId, int listingId,
        long propertyId, CancellationToken ct)
    {
        var list = await db.VariationLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct);
        if (list is null) return (null, null, "That list no longer exists.");
        var listing = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing?.EtsyState is null) return (null, null, "That listing hasn't been imported.");
        var model = ListingEditModel.Parse(listing.EtsyState.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
        if (model.Local(ListingFields.Inventory) is not { } inventory
            || VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == propertyId) is not { } variation)
            return (null, null, "This listing doesn't have that variation.");
        if (await db.ListingVariationLists.Where(l => l.ListingId == listingId && l.PropertyId == propertyId)
                .Select(l => l.VariationList.Name).FirstOrDefaultAsync(ct) is { } other)
            return (null, null, $"{variation.Name} already follows {other}; unlink it first.");
        if (await db.ListingVariationLists.AnyAsync(l => l.ListingId == listingId && l.VariationListId == listId, ct))
            return (null, null, $"Another variation of this listing already follows {list.Name}.");
        if (model.DriftFields.Contains(ListingFields.Inventory))
            return (null, null, "This listing's variations changed on Etsy; resolve that first.");
        return (list, inventory, null);
    }

    public async Task UnlinkAsync(int listId, int listingId, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ListingVariationLists.Where(l => l.VariationListId == listId && l.ListingId == listingId).ExecuteDeleteAsync(ct);
        await audit.WriteAsync("variation-list.unlinked", nameof(VariationList), listId.ToString(), $"listing {listingId} (keeps its variation)", user, ct);
    }

    /// <summary>
    /// Brings every linked listing in line with the list, as drafts (nothing is sent to Etsy). Listings whose variations
    /// changed on Etsy, or that lost the variation, are skipped. Returns the listings changed. With <paramref name="listingId"/>,
    /// only that listing (the editor's "Apply list now").
    /// </summary>
    public async Task<int> ApplyAsync(int listId, string? user, CancellationToken ct = default, int? listingId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.VariationLists.AsNoTracking().FirstAsync(l => l.Id == listId, ct);
        var links = await db.ListingVariationLists.Where(l => l.VariationListId == listId && (listingId == null || l.ListingId == listingId))
            .Include(l => l.Listing).ThenInclude(l => l.EtsyState).ToListAsync(ct);
        var changed = 0;
        foreach (var link in links.Where(l => l.Listing.EtsyState is not null))
        {
            var model = ListingEditModel.Parse(link.Listing.EtsyState!.ManagedJson, link.Listing.EtsyState.BaseJson, link.Listing.DraftJson);
            if (model.DriftFields.Contains(ListingFields.Inventory) || model.Local(ListingFields.Inventory) is not { } inventory
                || VariationEditor.Variations(inventory).All(v => v.PropertyId != link.PropertyId)) continue;
            var plan = Plan(list, link, inventory);
            if (plan.Changes.Count > 0)
            {
                await editor.SaveAsync(link.ListingId, new Dictionary<string, JsonNode?> { [ListingFields.Inventory] = plan.Inventory }, user, ct);
                changed++;
            }
            link.AppliedOptions = plan.Applied;
        }
        await db.SaveChangesAsync(ct);
        if (changed > 0)
            await audit.WriteAsync("variation-list.applied", nameof(VariationList), listId.ToString(), $"{list.Name}: {changed} listing draft(s) updated", user, ct);
        return changed;
    }

    /// <summary>The review before pushing: each linked listing with unpushed variation edits, with the editor's own preview.</summary>
    public async Task<IReadOnlyList<TemplatePushItem>> ReviewAsync(int listId, CancellationToken ct = default)
    {
        var items = new List<TemplatePushItem>();
        foreach (var use in (await UsesAsync(listId, ct)).Where(u => u.InventoryPending).DistinctBy(u => u.ListingId))
            items.Add(new TemplatePushItem(use.ListingId, use.Title, use.ShopName, await editor.PreviewAsync(use.ListingId, ct)));
        return items;
    }

    /// <summary>Queues the normal push for every reviewed listing. Returns the outcome per listing.</summary>
    public async Task<IReadOnlyList<(int ListingId, string Title, int? PushId, IReadOnlyList<string> Blockers)>> PushAsync(int listId,
        string? user, CancellationToken ct = default)
    {
        var results = new List<(int, string, int?, IReadOnlyList<string>)>();
        foreach (var item in await ReviewAsync(listId, ct))
        {
            var (pushId, blockers) = await editor.RequestPushAsync(item.ListingId, user, ct);
            results.Add((item.ListingId, item.Title, pushId, blockers));
        }
        return results;
    }
}

/// <summary>A listing in a template's push review, with the editor's own preview of everything its push would send.</summary>
public sealed record TemplatePushItem(int ListingId, string Title, string ShopName, PushPreview Preview);
