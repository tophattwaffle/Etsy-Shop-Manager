using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Templates;

public enum VariationPricingStrategy
{
    TakeHighest,
    TakeLowest,
    PickPerListing,
}

public sealed record ListingPricingItem(int ListingId, string Title, string ShopName, string CurrencyCode, decimal MinPrice,
    decimal MaxPrice, IReadOnlyList<string> NewOptions);

public sealed record ListingOptionPriceOverride(int ListingId, string ListingTitle, decimal Price);

public sealed record ApplyVariationTemplateResult(VariationPricingStrategy Strategy, IReadOnlyDictionary<int, decimal>? CustomPrices);

public enum VariationListUseStatus
{
    UpToDate,
    /// <summary>The list changed since it was last applied; applying changes the listing's variation.</summary>
    HasChanges,
    /// <summary>The listing no longer has the linked variation.</summary>
    VariationMissing,
    /// <summary>The listing's variations changed on Etsy since the last sync; resolve that in the editor first.</summary>
    ChangedOnEtsy,
    /// <summary>Applying would give the variation more options than Etsy allows; nothing is changed until some are switched off.</summary>
    TooManyOptions,
}

/// <summary>What applying a list would do to one listing's local inventory.</summary>
/// <param name="Changes">The changes in words ("Adds Galaxy Purple", "Makes Neon Green unavailable").</param>
/// <param name="Notes">Options left alone on this listing and why.</param>
public sealed record VariationListPlan(JsonObject Inventory, List<VariationListOption> Applied, IReadOnlyList<string> Changes,
    IReadOnlyList<string> Notes);

/// <summary>A list a listing's variation follows, with its link (the options as last applied).</summary>
public sealed record VariationListSource(VariationList List, ListingVariationList Link);

/// <summary>What applying every list a variation follows would do (see <see cref="VariationListService.PlanCombined"/>).</summary>
/// <param name="Applied">The options each source now reflects, in the order of the sources.</param>
/// <param name="Blocker">Why nothing can be applied (too many options for Etsy), or null.</param>
public sealed record VariationListCombinedPlan(JsonObject Inventory, IReadOnlyList<List<VariationListOption>> Applied, IReadOnlyList<string> Changes,
    IReadOnlyList<string> Notes, string? Blocker);

/// <param name="CombinedWith">The other lists the variation follows, in order (empty when it follows only this one).</param>
public sealed record VariationListUse(int ListingId, string Title, int ShopId, string ShopName, long EtsyListingId,
    string State, bool IsMissingOnEtsy, IReadOnlyList<string> Skus, IReadOnlyList<string> Tags,
    long PropertyId, string VariationName,
    VariationListUseStatus Status, IReadOnlyList<string> Changes, IReadOnlyList<string> Notes, bool InventoryPending,
    IReadOnlyList<string>? CombinedWith = null) : ITemplateListingItem;

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

/// <summary>
/// A listing variation not linked to the list whose values are mostly in it. When the variation already follows other lists,
/// only the values those lists don't cover are counted, and linking combines this list with them (<paramref name="CombinedWith"/>).
/// </summary>
public sealed record VariationListCandidate(int ListingId, string Title, int ShopId, string ShopName, long EtsyListingId,
    string State, bool IsMissingOnEtsy, IReadOnlyList<string> Skus, IReadOnlyList<string> Tags,
    long PropertyId, string VariationName,
    int InList, int Values, IReadOnlyList<string>? CombinedWith = null) : ITemplateListingItem;

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
    public const int MaxOptions = VariationEditor.MaxValues;

    /// <summary>
    /// The plan for one listing (pure). Options are taken in the list's order; new ones go at the end of the listing's
    /// values (Etsy shows new values last). <paramref name="reorder"/> false leaves the value order alone (the combined
    /// plan orders once, for all the lists).
    /// </summary>
    public static VariationListPlan Plan(VariationList list, ListingVariationList link, JsonNode localInventory,
        VariationPricingStrategy strategy = VariationPricingStrategy.TakeHighest, decimal? customPrice = null,
        string? skuPattern = null, string? baseSku = null, bool reorder = true)
    {
        var inventory = (JsonObject)localInventory.DeepClone();
        var changes = new List<string>();
        var notes = new List<string>();
        var applied = link.AppliedOptions.ToDictionary(o => o.Id);
        var label = VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == link.PropertyId)?.Name ?? "the variation";
        string? Has(string name) => VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == link.PropertyId)?
            .Values.FirstOrDefault(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase));

        var pattern = skuPattern ?? (string?)localInventory["sku_pattern"] ?? link.Listing?.SkuPattern;
        var baseSkuVal = baseSku ?? (string?)localInventory["base_sku"] ?? link.Listing?.Skus.FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";

        // Removals: options in AppliedOptions no longer in the template (R1).
        var removedFromTemplate = link.AppliedOptions.Where(b => list.Options.All(o => o.Id != b.Id)).ToList();
        var retainedRemoved = new List<VariationListOption>();
        foreach (var before in removedFromTemplate)
        {
            var current = Has(before.Name);
            if (current is null)
            {
                notes.Add($"{before.Name} was already removed from this listing by hand.");
                continue;
            }
            var variationValues = VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == link.PropertyId)?.Values ?? [];
            if (variationValues.Count <= 1)
            {
                notes.Add($"Cannot remove {current}: it is the last remaining value on this variation.");
                retainedRemoved.Add(before);
                continue;
            }
            try
            {
                inventory = VariationEditor.RemoveValue(inventory, link.PropertyId, current);
                changes.Add($"Removes {current}");
            }
            catch (ArgumentException ex)
            {
                notes.Add(ex.Message);
                retainedRemoved.Add(before);
            }
        }

        var newlyAdded = new List<string>();
        foreach (var option in list.Options)
        {
            if (!applied.TryGetValue(option.Id, out var before))
            {
                // New to this listing: add it when it's on and the listing doesn't have it yet (it may have it of its own).
                if (!option.Enabled || Has(option.Name) is not null) continue;
                try
                {
                    inventory = VariationEditor.AddValue(inventory, link.PropertyId, option.Name, pattern, baseSkuVal);
                    changes.Add($"Adds {option.Name}");
                    newlyAdded.Add(option.Name);
                }
                catch (ArgumentException ex) { notes.Add(ex.Message); }
                continue;
            }
            var current = Has(before.Name);
            if (current is null)
            {
                notes.Add($"{before.Name} was removed from this listing by hand, so the template leaves it out.");
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

        if (newlyAdded.Count > 0)
        {
            inventory = VariationEditor.ApplyOptionPricing(inventory, link.PropertyId, newlyAdded, strategy, customPrice);
        }

        var currentListingValues = VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == link.PropertyId)?.Values ?? [];

        // Compare order of enabled template options
        var templateOrder = list.Options.Where(o => o.Enabled).Select(o => o.Name).ToList();
        var appliedOrder = link.AppliedOptions.Where(o => o.Enabled).Select(o => o.Name).ToList();

        var templateOrderChanged = false;
        if (appliedOrder.Count > 1)
        {
            var commonApplied = appliedOrder.Where(a => templateOrder.Any(t => string.Equals(t, a, StringComparison.OrdinalIgnoreCase))).ToList();
            var commonTemplate = templateOrder.Where(t => appliedOrder.Any(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase))).ToList();
            templateOrderChanged = commonApplied.Count > 1 && !commonApplied.SequenceEqual(commonTemplate, StringComparer.OrdinalIgnoreCase);
        }

        var voList = VariationEditor.GetValueOrder(inventory, link.PropertyId);
        var existingVoList = voList.Where(v => !newlyAdded.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
        var voTemplateOrder = existingVoList.Where(v => templateOrder.Any(t => string.Equals(t, v, StringComparison.OrdinalIgnoreCase))).ToList();
        var expectedTemplateOrder = templateOrder.Where(t => existingVoList.Any(v => string.Equals(v, t, StringComparison.OrdinalIgnoreCase))).ToList();
        var inventoryOrderDiffers = voTemplateOrder.Count > 1 && !voTemplateOrder.SequenceEqual(expectedTemplateOrder, StringComparer.OrdinalIgnoreCase);

        var desiredOrder = VariationEditor.OrderValuesByTemplate(currentListingValues, templateOrder);

        if (!reorder) { }
        else if (templateOrderChanged || inventoryOrderDiffers)
        {
            try
            {
                inventory = VariationEditor.ReorderValues(inventory, link.PropertyId, desiredOrder);
                changes.Add($"Reorders options in {label}");
            }
            catch (ArgumentException ex) { notes.Add(ex.Message); }
        }
        else if (newlyAdded.Count > 0 && !currentListingValues.SequenceEqual(desiredOrder, StringComparer.Ordinal))
        {
            try
            {
                inventory = VariationEditor.ReorderValues(inventory, link.PropertyId, desiredOrder);
            }
            catch (ArgumentException ex) { notes.Add(ex.Message); }
        }

        // What the listing now reflects: retained removed options plus each list option it has, under the name it has it.
        var newApplied = new List<VariationListOption>(retainedRemoved);
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

    /// <summary>
    /// The plan for a variation that follows one or more lists, in order (pure). One list is exactly <see cref="Plan"/>.
    /// With several, each list's changes are worked out in turn as <see cref="Plan"/> does, then the values are ordered
    /// once: the lists' switched-on options in list order, the listing's own values after them. An option in more than one
    /// list belongs to the first (a note says so): later lists neither add, rename, switch nor remove it. A plan that would
    /// leave the variation with more than Etsy's <see cref="MaxOptions"/> changes nothing and says why (<c>Blocker</c>).
    /// </summary>
    public static VariationListCombinedPlan PlanCombined(IReadOnlyList<VariationListSource> sources, JsonNode localInventory,
        VariationPricingStrategy strategy = VariationPricingStrategy.TakeHighest, decimal? customPrice = null,
        string? skuPattern = null, string? baseSku = null)
    {
        if (sources.Count == 0) throw new ArgumentException("A variation needs at least one list to follow.");
        var propertyId = sources[0].Link.PropertyId;
        int Count(JsonNode inv) => VariationEditor.Variations(inv).FirstOrDefault(v => v.PropertyId == propertyId)?.Values.Count ?? 0;
        VariationListCombinedPlan Limited(VariationListCombinedPlan plan)
        {
            var count = Count(plan.Inventory);
            if (count <= MaxOptions || count <= Count(localInventory)) return plan;
            var names = string.Join(" and ", sources.Select(s => s.List.Name));
            return new((JsonObject)localInventory.DeepClone(), [.. sources.Select(s => s.Link.AppliedOptions)], [], plan.Notes,
                $"Applying {names} would give this variation {count} options; Etsy allows at most {MaxOptions}. " +
                "Switch some options off, or stop using one of the templates on this listing.");
        }

        if (sources.Count == 1)
        {
            var single = Plan(sources[0].List, sources[0].Link, localInventory, strategy, customPrice, skuPattern, baseSku);
            return Limited(new(single.Inventory, [single.Applied], single.Changes, single.Notes, null));
        }

        var inventory = (JsonObject)localInventory.DeepClone();
        var label = VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == propertyId)?.Name ?? "the variation";
        var originalValues = VariationEditor.GetValueOrder(inventory, propertyId);
        var changes = new List<string>();
        var notes = new List<string>();
        var applied = new List<List<VariationListOption>>();
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // option name → the list it belongs to
        var order = new List<string>();
        foreach (var (list, link) in sources)
        {
            var duplicates = list.Options.Where(o => claimed.ContainsKey(o.Name)).ToList();
            foreach (var d in duplicates)
                notes.Add($"{d.Name} is in both {claimed[d.Name]} and {list.Name}; the listing has it once, following {claimed[d.Name]}.");
            var duplicateIds = duplicates.Select(d => d.Id).ToHashSet();
            var effective = new VariationList { Id = list.Id, Name = list.Name, Options = [.. list.Options.Where(o => !duplicateIds.Contains(o.Id))] };
            var effectiveLink = new ListingVariationList
            {
                PropertyId = propertyId, Listing = link.Listing,
                AppliedOptions = [.. link.AppliedOptions.Where(o => !duplicateIds.Contains(o.Id) && !claimed.ContainsKey(o.Name))],
            };
            var plan = Plan(effective, effectiveLink, inventory, strategy, customPrice, skuPattern, baseSku, reorder: false);
            inventory = plan.Inventory;
            changes.AddRange(plan.Changes.Skip(1)); // without the "Color:" heading
            notes.AddRange(plan.Notes);
            applied.Add(plan.Applied);
            foreach (var o in list.Options) claimed.TryAdd(o.Name, list.Name);
            order.AddRange(effective.Options.Where(o => o.Enabled).Select(o => o.Name));
        }

        // One order for all the lists. A reorder of values the listing already had is a change of its own; new values
        // only need putting in their place.
        if (VariationEditor.Variations(inventory).Any(v => v.PropertyId == propertyId))
        {
            var current = VariationEditor.GetValueOrder(inventory, propertyId);
            var desired = VariationEditor.OrderValuesByTemplate(current, order);
            if (!desired.SequenceEqual(current, StringComparer.Ordinal))
            {
                bool Existing(string v) => originalValues.Contains(v, StringComparer.OrdinalIgnoreCase);
                var existingMoved = !current.Where(Existing).SequenceEqual(desired.Where(Existing), StringComparer.OrdinalIgnoreCase);
                try
                {
                    inventory = VariationEditor.ReorderValues(inventory, propertyId, desired);
                    if (existingMoved) changes.Add($"Reorders options in {label}");
                }
                catch (ArgumentException ex) { notes.Add(ex.Message); }
            }
        }
        if (changes.Count > 0) changes.Insert(0, $"{label}:");
        return Limited(new(inventory, applied, changes, notes, null));
    }

    /// <summary>The lists a listing's variation follows, in order (tracked unless <paramref name="tracked"/> is false).</summary>
    private static async Task<List<ListingVariationList>> FollowedAsync(AppDbContext db, int listingId, long propertyId, CancellationToken ct,
        bool tracked = true)
    {
        var query = db.ListingVariationLists.Include(l => l.VariationList).Include(l => l.Listing)
            .Where(l => l.ListingId == listingId && l.PropertyId == propertyId);
        return await (tracked ? query : query.AsNoTracking()).OrderBy(l => l.Position).ThenBy(l => l.Id).ToListAsync(ct);
    }

    private static List<VariationListSource> Sources(IEnumerable<ListingVariationList> links) =>
        [.. links.OrderBy(l => l.Position).ThenBy(l => l.Id).Select(l => new VariationListSource(l.VariationList, l))];

    /// <summary>The values of a variation not covered by the given lists (by option name).</summary>
    private static List<string> Uncovered(IReadOnlyList<string> values, IEnumerable<VariationList> lists)
    {
        var covered = lists.SelectMany(l => l.Options).Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. values.Where(v => !covered.Contains(v))];
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
                uses.Count(u => u.Status is VariationListUseStatus.VariationMissing or VariationListUseStatus.ChangedOnEtsy
                    or VariationListUseStatus.TooManyOptions), list.UpdatedAtUtc));
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
        if (name.Length == 0) return (null, "Give the template a name.");
        if (name.Length > MaxNameLength) return (null, $"The name is {name.Length} characters; keep it to {MaxNameLength}.");
        if (opts.Count == 0) return (null, "The template needs at least one option.");
        if (opts.Any(o => o.Name.Length == 0)) return (null, "Every option needs a name.");
        if (opts.GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
            return (null, $"\"{dup.Key}\" is in the template twice.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.VariationLists.AnyAsync(l => l.Id != id && l.Name == name, ct)) return (null, $"There's already a template called \"{name}\".");
        var now = time.GetUtcNow().UtcDateTime;
        var list = id is { } existing ? await db.VariationLists.FirstOrDefaultAsync(l => l.Id == existing, ct) : null;
        if (id is not null && list is null) return (null, "That template no longer exists.");
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
        // The other lists each linked variation follows too (a combination is planned as a whole).
        var listingIds = links.Select(l => l.ListingId).ToList();
        var all = await db.ListingVariationLists.AsNoTracking().Include(l => l.VariationList)
            .Where(l => listingIds.Contains(l.ListingId)).ToListAsync(ct);
        return [.. links.Where(l => l.Listing.EtsyState is not null).Select(l =>
            {
                var followed = all.Where(o => o.ListingId == l.ListingId && o.PropertyId == l.PropertyId)
                    .Select(o => o.VariationListId == list.Id ? l : WithListing(o, l.Listing)).ToList();
                l.VariationList = list;
                return Use(list, l, Sources(followed));
            })
            .OrderBy(u => u.Status == VariationListUseStatus.UpToDate).ThenBy(u => u.Title, StringComparer.OrdinalIgnoreCase)];
    }

    private static ListingVariationList WithListing(ListingVariationList link, Listing listing)
    {
        link.Listing = listing;
        return link;
    }

    private static VariationListUse Use(VariationList list, ListingVariationList link, IReadOnlyList<VariationListSource> sources)
    {
        var listing = link.Listing;
        var combinedWith = sources.Where(s => s.List.Id != list.Id).Select(s => s.List.Name).ToList();
        var model = ListingEditModel.Parse(listing.EtsyState!.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
        var inventory = model.Local(ListingFields.Inventory) ?? new JsonObject();
        var pending = !JsonNode.DeepEquals(inventory, model.Etsy[ListingFields.Inventory]);
        var variation = VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == link.PropertyId);
        if (model.DriftFields.Contains(ListingFields.Inventory))
            return new(listing.Id, listing.Title, listing.ShopId, listing.Shop.Name, listing.EtsyListingId, listing.State, listing.IsMissingOnEtsy, listing.Skus, listing.Tags, link.PropertyId, variation?.Name ?? "", VariationListUseStatus.ChangedOnEtsy, [], [], pending, combinedWith);
        if (variation is null)
            return new(listing.Id, listing.Title, listing.ShopId, listing.Shop.Name, listing.EtsyListingId, listing.State, listing.IsMissingOnEtsy, listing.Skus, listing.Tags, link.PropertyId, "", VariationListUseStatus.VariationMissing, [],
                ["The linked variation isn't on this listing any more. Unlink it, or add the variation back in the editor."], pending, combinedWith);
        var plan = PlanCombined(sources, inventory);
        if (plan.Blocker is not null)
            return new(listing.Id, listing.Title, listing.ShopId, listing.Shop.Name, listing.EtsyListingId, listing.State, listing.IsMissingOnEtsy, listing.Skus, listing.Tags, link.PropertyId, variation.Name ?? "",
                VariationListUseStatus.TooManyOptions, [], [plan.Blocker, .. plan.Notes], pending, combinedWith);
        return new(listing.Id, listing.Title, listing.ShopId, listing.Shop.Name, listing.EtsyListingId, listing.State, listing.IsMissingOnEtsy, listing.Skus, listing.Tags, link.PropertyId, variation.Name ?? "",
            plan.Changes.Count > 0 ? VariationListUseStatus.HasChanges : VariationListUseStatus.UpToDate, plan.Changes, plan.Notes, pending, combinedWith);
    }

    /// <summary>
    /// Listing variations not linked to this list whose values are mostly in it. A variation that already follows other
    /// lists counts only the values they don't cover (linking then combines the lists); a listing that already uses this
    /// list on any variation isn't offered.
    /// </summary>
    public async Task<IReadOnlyList<VariationListCandidate>> CandidatesAsync(int listId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.VariationLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct);
        if (list is null) return [];
        var links = await db.ListingVariationLists.AsNoTracking().Include(l => l.VariationList).ToListAsync(ct);
        var usingList = links.Where(l => l.VariationListId == listId).Select(l => l.ListingId).ToHashSet();
        var names = list.Options.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listings = await db.Listings.AsNoTracking().Include(l => l.EtsyState).Include(l => l.Shop)
            .Where(l => l.EtsyState != null).ToListAsync(ct);
        var result = new List<VariationListCandidate>();
        foreach (var l in listings.Where(l => !usingList.Contains(l.Id)))
        {
            var model = ListingEditModel.Parse(l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson);
            if (model.Local(ListingFields.Inventory) is not { } inventory) continue;
            foreach (var v in VariationEditor.Variations(inventory))
            {
                var followed = links.Where(k => k.ListingId == l.Id && k.PropertyId == v.PropertyId)
                    .OrderBy(k => k.Position).ThenBy(k => k.Id).Select(k => k.VariationList).ToList();
                var values = Uncovered(v.Values, followed);
                var inList = values.Count(names.Contains);
                if (inList >= MinOverlap && inList * 2 >= values.Count)
                    result.Add(new VariationListCandidate(l.Id, l.Title, l.ShopId, l.Shop.Name, l.EtsyListingId, l.State, l.IsMissingOnEtsy, l.Skus, l.Tags, v.PropertyId, v.Name ?? $"Variation {v.PropertyId}", inList, values.Count,
                        [.. followed.Select(f => f.Name)]));
            }
        }
        return [.. result.OrderByDescending(c => (double)c.InList / c.Values).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// A new link of a variation to the list, after the lists it already follows (<paramref name="followed"/>, in order): its
    /// position, and the options it starts with (those the listing has that the earlier lists don't cover). Also the plan
    /// for the whole combination, so callers can refuse one that goes over Etsy's limit.
    /// </summary>
    private static (ListingVariationList Link, VariationListCombinedPlan Plan) NewLink(VariationList list, Listing listing, long propertyId,
        JsonNode inventory, IReadOnlyList<ListingVariationList> followed, DateTime now)
    {
        var covered = followed.SelectMany(f => f.VariationList.Options).Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var link = new ListingVariationList
        {
            ListingId = listing.Id, Listing = listing, VariationListId = list.Id, VariationList = list, PropertyId = propertyId,
            Position = followed.Count == 0 ? 0 : followed.Max(f => f.Position) + 1,
            AppliedOptions = [.. InitialApplied(list, propertyId, inventory).Where(o => !covered.Contains(o.Name))], LinkedAtUtc = now,
        };
        return (link, PlanCombined(Sources([.. followed, link]), inventory));
    }

    /// <summary>
    /// Links listing variations to the list. A variation that follows no list yet has its values reordered to follow the
    /// template (saved as a draft if the order changes); one that already follows other lists gets this one after them,
    /// and the next apply orders them together. Variations whose combined options would go over Etsy's limit are skipped.
    /// Returns how many were linked.
    /// </summary>
    public async Task<int> LinkAsync(int listId, IEnumerable<(int ListingId, long PropertyId)> variations, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.VariationLists.AsNoTracking().FirstAsync(l => l.Id == listId, ct);
        var templateOrder = list.Options.Select(o => o.Name).ToList();
        var count = 0;
        foreach (var (listingId, propertyId) in variations.Distinct())
        {
            var listing = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstOrDefaultAsync(l => l.Id == listingId, ct);
            if (listing?.EtsyState is null) continue;
            var model = ListingEditModel.Parse(listing.EtsyState.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
            if (model.Local(ListingFields.Inventory) is not { } inventory
                || VariationEditor.Variations(inventory).All(v => v.PropertyId != propertyId)) continue;
            if (await db.ListingVariationLists.AnyAsync(l => l.ListingId == listingId && l.VariationListId == listId, ct)) continue;
            var followed = await FollowedAsync(db, listingId, propertyId, ct, tracked: false);
            if (followed.Count > 0)
            {
                var (combined, plan) = NewLink(list, listing, propertyId, inventory, followed, time.GetUtcNow().UtcDateTime);
                if (plan.Blocker is not null) continue;
                (combined.Listing, combined.VariationList) = (null!, null!);
                db.ListingVariationLists.Add(combined);
                count++;
                continue;
            }

            var currentValues = VariationEditor.GetValueOrder(inventory, propertyId);
            var desiredOrder = VariationEditor.OrderValuesByTemplate(currentValues, templateOrder);
            if (!desiredOrder.SequenceEqual(currentValues, StringComparer.Ordinal))
            {
                var reordered = VariationEditor.ReorderValues(inventory, propertyId, desiredOrder);
                await editor.SaveAsync(listingId, new Dictionary<string, JsonNode?> { [ListingFields.Inventory] = reordered }, user, ct);
                inventory = reordered;
            }

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
        if (options.Count == 0) throw new ArgumentException("Every option in the template is switched off.");
        if (options.Count > MaxOptions) throw new ArgumentException($"The template has {options.Count} options switched on; Etsy allows at most {MaxOptions} in a variation.");
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
        if (list is null) return (null, null, "That template no longer exists.");
        var listing = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing?.EtsyState is null) return (null, null, "That listing hasn't been imported.");
        if (await db.ListingVariationLists.AnyAsync(l => l.ListingId == listingId && l.VariationListId == listId, ct))
            return (null, null, $"{listing.Title} already uses this template.");
        var model = ListingEditModel.Parse(listing.EtsyState.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
        if (model.DriftFields.Contains(ListingFields.Inventory))
            return (null, null, $"The variations of {listing.Title} changed on Etsy; resolve that in the editor first.");
        return model.Local(ListingFields.Inventory) is { } inventory
            ? (list, inventory, null)
            : (null, null, $"{listing.Title} has no inventory imported; refresh it from Etsy first.");
    }

    /// <summary>The lists a listing's variations follow (a variation's lists in the order they're combined).</summary>
    public async Task<IReadOnlyList<VariationListLink>> ListingLinksAsync(int listingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ListingVariationLists.AsNoTracking().Where(l => l.ListingId == listingId)
            .OrderBy(l => l.PropertyId).ThenBy(l => l.Position).ThenBy(l => l.Id)
            .Select(l => new VariationListLink(l.VariationListId, l.VariationList.Name, l.PropertyId)).ToListAsync(ct);
    }

    /// <summary>
    /// What linking one of a listing's variations to a list means (nothing is saved): how many of its values are in the
    /// list, a warning when few are, and what applying the list would then change. Any list can be chosen here (no match
    /// threshold, unlike <see cref="CandidatesAsync"/>). When the variation already follows other lists, the list is
    /// combined with them (after them): only the values they don't cover are counted, and the changes are the combination's.
    /// </summary>
    public async Task<(VariationListLinkPreview? Preview, string? Error)> PreviewLinkAsync(int listId, int listingId, long propertyId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (list, listing, inventory, followed, error) = await LinkSourceAsync(db, listId, listingId, propertyId, ct);
        if (error is not null) return (null, error);
        var variation = VariationEditor.Variations(inventory!).Single(v => v.PropertyId == propertyId);
        var names = list!.Options.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (_, plan) = NewLink(list, listing!, propertyId, inventory!, followed!, time.GetUtcNow().UtcDateTime);
        if (plan.Blocker is not null) return (null, plan.Blocker);
        if (followed!.Count > 0)
        {
            var values = Uncovered(variation.Values, followed.Select(f => f.VariationList));
            return (new VariationListLinkPreview(values.Count(names.Contains), values.Count, null, plan.Changes, plan.Notes), null);
        }
        var inList = variation.Values.Count(names.Contains);
        string? warning = inList >= MinOverlap && inList * 2 >= variation.Values.Count ? null
            : inList == 0 ? $"None of {variation.Name}'s values are in {list.Name}; applying it adds all of the template's options next to them."
            : $"Only {inList} of {variation.Values.Count} values are in {list.Name}; the others stay as the listing's own.";
        return (new VariationListLinkPreview(inList, variation.Values.Count, warning, plan.Changes, plan.Notes), null);
    }

    /// <summary>
    /// Links one variation of a listing to a list. A variation that follows no list yet has its values reordered to follow
    /// the template (saved as a draft if the order changes). One that already follows other lists gets this one after
    /// them (combined); that only records the link, and the next apply brings in its options and orders them together.
    /// Refused when the combined options would go over Etsy's limit.
    /// </summary>
    public async Task<string?> LinkVariationAsync(int listId, int listingId, long propertyId, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (list, listing, inventory, followed, error) = await LinkSourceAsync(db, listId, listingId, propertyId, ct);
        if (error is not null) return error;
        var (combined, plan) = NewLink(list!, listing!, propertyId, inventory!, followed!, time.GetUtcNow().UtcDateTime);
        if (plan.Blocker is not null) return plan.Blocker;
        if (followed!.Count > 0)
        {
            (combined.Listing, combined.VariationList) = (null!, null!);
            db.ListingVariationLists.Add(combined);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("variation-list.linked", nameof(VariationList), listId.ToString(),
                $"{list!.Name}: listing {listingId}, combined with {string.Join(", ", followed.Select(f => f.VariationList.Name))}, from its editor", user, ct);
            return null;
        }

        var currentValues = VariationEditor.GetValueOrder(inventory!, propertyId);
        var templateOrder = list!.Options.Select(o => o.Name).ToList();
        var desiredOrder = VariationEditor.OrderValuesByTemplate(currentValues, templateOrder);
        if (!desiredOrder.SequenceEqual(currentValues, StringComparer.Ordinal))
        {
            var reordered = VariationEditor.ReorderValues(inventory!, propertyId, desiredOrder);
            await editor.SaveAsync(listingId, new Dictionary<string, JsonNode?> { [ListingFields.Inventory] = reordered }, user, ct);
            inventory = reordered;
        }

        db.ListingVariationLists.Add(new ListingVariationList
        {
            ListingId = listingId, VariationListId = listId, PropertyId = propertyId,
            AppliedOptions = InitialApplied(list!, propertyId, inventory!), LinkedAtUtc = time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("variation-list.linked", nameof(VariationList), listId.ToString(), $"{list!.Name}: listing {listingId}, from its editor", user, ct);
        return null;
    }

    private static async Task<(VariationList? List, Listing? Listing, JsonNode? Inventory, List<ListingVariationList>? Followed, string? Error)>
        LinkSourceAsync(AppDbContext db, int listId, int listingId, long propertyId, CancellationToken ct)
    {
        var list = await db.VariationLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct);
        if (list is null) return (null, null, null, null, "That list no longer exists.");
        var listing = await db.Listings.AsNoTracking().Include(l => l.EtsyState).FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing?.EtsyState is null) return (null, null, null, null, "That listing hasn't been imported.");
        var model = ListingEditModel.Parse(listing.EtsyState.ManagedJson, listing.EtsyState.BaseJson, listing.DraftJson);
        if (model.Local(ListingFields.Inventory) is not { } inventory
            || VariationEditor.Variations(inventory).FirstOrDefault(v => v.PropertyId == propertyId) is not { } variation)
            return (null, null, null, null, "This listing doesn't have that variation.");
        var followed = await FollowedAsync(db, listingId, propertyId, ct, tracked: false);
        if (followed.Any(f => f.VariationListId == listId))
            return (null, null, null, null, $"{variation.Name} already follows {list.Name}.");
        if (await db.ListingVariationLists.AnyAsync(l => l.ListingId == listingId && l.VariationListId == listId, ct))
            return (null, null, null, null, $"Another variation of this listing already follows {list.Name}.");
        if (model.DriftFields.Contains(ListingFields.Inventory))
            return (null, null, null, null, "This listing's variations changed on Etsy; resolve that first.");
        return (list, listing, inventory, followed, null);
    }

    /// <summary>
    /// Stops a listing following the list. Its values stay on the listing as its own; other lists the variation follows
    /// are unaffected.
    /// </summary>
    public async Task UnlinkAsync(int listId, int listingId, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ListingVariationLists.Where(l => l.VariationListId == listId && l.ListingId == listingId).ExecuteDeleteAsync(ct);
        await audit.WriteAsync("variation-list.unlinked", nameof(VariationList), listId.ToString(), $"listing {listingId} (keeps its variation)", user, ct);
    }

    /// <summary>
    /// Moves a list earlier (<paramref name="by"/> -1) or later (+1) among the lists a listing's variation follows. Only the
    /// order is recorded: the next apply reorders the values and settles options that are in more than one list.
    /// </summary>
    public async Task MoveAsync(int listId, int listingId, int by, string? user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var link = await db.ListingVariationLists.AsNoTracking().FirstOrDefaultAsync(l => l.VariationListId == listId && l.ListingId == listingId, ct);
        if (link is null) return;
        var followed = await FollowedAsync(db, listingId, link.PropertyId, ct);
        var index = followed.FindIndex(f => f.VariationListId == listId);
        var target = index + by;
        if (target < 0 || target >= followed.Count) return;
        (followed[index], followed[target]) = (followed[target], followed[index]);
        for (var i = 0; i < followed.Count; i++) followed[i].Position = i;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("variation-list.reordered", nameof(VariationList), listId.ToString(),
            $"listing {listingId}: {string.Join(", then ", followed.Select(f => f.VariationList.Name))}", user, ct);
    }

    /// <summary>
    /// Listings linked to this list that have per-variation pricing on the linked variation and are adding new options.
    /// </summary>
    public async Task<IReadOnlyList<ListingPricingItem>> ListingsNeedingPricingAsync(int listId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.VariationLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct);
        if (list is null) return [];

        var links = await db.ListingVariationLists.AsNoTracking().Where(l => l.VariationListId == listId)
            .Include(l => l.Listing).ThenInclude(l => l.EtsyState)
            .Include(l => l.Listing).ThenInclude(l => l.Shop)
            .ToListAsync(ct);

        var result = new List<ListingPricingItem>();
        foreach (var link in links.Where(l => l.Listing.EtsyState is not null && !l.Listing.IsMissingOnEtsy))
        {
            var model = ListingEditModel.Parse(link.Listing.EtsyState!.ManagedJson, link.Listing.EtsyState.BaseJson, link.Listing.DraftJson);
            if (model.DriftFields.Contains(ListingFields.Inventory) || model.Local(ListingFields.Inventory) is not { } inventory)
                continue;

            var priceOn = (inventory[VariationEditor.PriceOnProperty]?.AsArray() ?? []).Select(x => (long)x!).ToHashSet();
            if (!priceOn.Contains(link.PropertyId)) continue;

            var plan = PlanCombined(Sources(await FollowedAsync(db, link.ListingId, link.PropertyId, ct, tracked: false)), inventory);
            if (plan.Blocker is not null) continue;
            var addedOptions = plan.Changes
                .Where(c => c.StartsWith("Adds ", StringComparison.OrdinalIgnoreCase))
                .Select(c => c["Adds ".Length..].Trim())
                .ToList();

            if (addedOptions.Count == 0) continue;

            var existingPrices = VariationEditor.GetExistingPrices(inventory, link.PropertyId, addedOptions);
            var min = existingPrices.Count > 0 ? existingPrices.Min() : 0.20m;
            var max = existingPrices.Count > 0 ? existingPrices.Max() : min;
            var currency = link.Listing.CurrencyCode ?? link.Listing.Shop?.CurrencyCode ?? "USD";

            result.Add(new ListingPricingItem(link.ListingId, link.Listing.Title, link.Listing.Shop?.Name ?? "", currency, min, max, addedOptions));
        }
        return [.. result.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Brings every linked listing in line with the list, as drafts (nothing is sent to Etsy). A variation that follows
    /// other lists too is brought in line with all of them together (the combination is one variation). Listings whose
    /// variations changed on Etsy, that lost the variation, or that would go over Etsy's option limit are skipped. Returns
    /// the listings changed. With <paramref name="listingId"/>, only that listing (the editor's "Apply list now").
    /// </summary>
    public async Task<int> ApplyAsync(int listId, string? user, VariationPricingStrategy strategy = VariationPricingStrategy.TakeHighest,
        IReadOnlyDictionary<int, decimal>? customPrices = null, CancellationToken ct = default, int? listingId = null)
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
            decimal? customPrice = customPrices is not null && customPrices.TryGetValue(link.ListingId, out var cp) ? cp : null;
            var followed = await FollowedAsync(db, link.ListingId, link.PropertyId, ct);
            var plan = PlanCombined(Sources(followed), inventory, strategy, customPrice);
            if (plan.Blocker is not null) continue;
            if (plan.Changes.Count > 0)
            {
                await editor.SaveAsync(link.ListingId, new Dictionary<string, JsonNode?> { [ListingFields.Inventory] = plan.Inventory }, user, ct);
                changed++;
            }
            var sources = Sources(followed);
            for (var i = 0; i < sources.Count; i++) sources[i].Link.AppliedOptions = plan.Applied[i];
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
