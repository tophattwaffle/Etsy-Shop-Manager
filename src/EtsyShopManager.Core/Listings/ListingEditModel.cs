using System.Text.Json.Nodes;

namespace EtsyShopManager.Core.Listings;

/// <summary>A managed field the editor knows about. Keys are the snake_case names in <see cref="ManagedListingState"/>'s JSON.</summary>
/// <param name="Editable">Can be changed in the editor and pushed to Etsy.</param>
/// <param name="Tracked">
/// Changes made on Etsy are flagged as drift (and block pushes) instead of being accepted. Untracked fields simply
/// follow Etsy, because the app can't edit them yet (photos, attributes…) or Etsy changes them on its own (state).
/// </param>
public sealed record ListingField(string Key, string Label, bool Editable, bool Tracked);

public static class ListingFields
{
    public const string Inventory = "inventory";
    public const string State = "state";

    public static readonly IReadOnlyList<ListingField> All =
    [
        new("title", "Title", true, true),
        new("description", "Description", true, true),
        new(Inventory, "Variations, prices and processing", true, true),
        new("tags", "Tags", true, true),
        new("materials", "Materials", true, true),
        new("taxonomy_id", "Category", true, true),
        new("shipping_profile_id", "Shipping profile", true, true),
        new("return_policy_id", "Return policy", true, true),
        new("shop_section_id", "Shop section", true, true),
        new("who_made", "Who made it", true, true),
        new("when_made", "When it was made", true, true),
        new("is_supply", "Craft supply", true, true),
        new("is_taxable", "Taxable", true, true),
        new("should_auto_renew", "Auto-renew", true, true),
        new("item_weight", "Weight", true, true),
        new("item_weight_unit", "Weight unit", true, true),
        new("item_length", "Length", true, true),
        new("item_width", "Width", true, true),
        new("item_height", "Height", true, true),
        new("item_dimensions_unit", "Dimension unit", true, true),
        new("production_partner_ids", "Production partners", true, true),
        // Etsy changes the state itself (sold out, expired), so it follows Etsy; it can still be set to active/inactive.
        new(State, "Status", true, false),
    ];

    private static readonly Dictionary<string, ListingField> ByKey = All.ToDictionary(f => f.Key);

    public static ListingField? Find(string key) => ByKey.GetValueOrDefault(key);

    public static bool IsTracked(string key) => ByKey.TryGetValue(key, out var f) && f.Tracked;
    public static bool IsEditable(string key) => ByKey.TryGetValue(key, out var f) && f.Editable;
}

/// <summary>
/// The three versions of a listing the editor works with, as canonical managed-field JSON:
/// <list type="bullet">
/// <item><b>Etsy</b>: the last state read from Etsy.</item>
/// <item><b>Base</b>: the state the app last agreed with Etsy (first import, after a push, after resolving drift).</item>
/// <item><b>Draft</b>: local edits not pushed yet (only the fields that were changed).</item>
/// </list>
/// The local version is the base with the draft on top. Drift = a tracked field where Etsy no longer matches the base
/// (changed on etsy.com). Pending = an editable field where the local version differs from Etsy (what a push sends).
/// </summary>
public sealed class ListingEditModel
{
    public JsonObject Etsy { get; }
    public JsonObject Base { get; }
    public JsonObject Draft { get; }

    public ListingEditModel(JsonObject etsy, JsonObject? baseState, JsonObject? draft)
    {
        Etsy = etsy;
        Base = Reconcile(etsy, baseState);
        Draft = draft ?? [];
        // Draft entries that no longer differ from the base are not edits.
        foreach (var key in Draft.Select(p => p.Key).ToList())
            if (!ListingFields.IsEditable(key) || JsonNode.DeepEquals(Draft[key], Base[key])) Draft.Remove(key);
    }

    public static ListingEditModel Parse(string etsyJson, string? baseJson, string? draftJson) =>
        new(ParseObject(etsyJson)!, ParseObject(baseJson), ParseObject(draftJson));

    private static JsonObject? ParseObject(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;

    /// <summary>
    /// The base after a fresh read from Etsy: the first read becomes the base; afterwards untracked fields follow Etsy
    /// and tracked ones keep their agreed value (so a change on Etsy shows up as drift).
    /// </summary>
    public static JsonObject Reconcile(JsonObject etsy, JsonObject? baseState)
    {
        if (baseState is null) return (JsonObject)etsy.DeepClone();
        var result = (JsonObject)baseState.DeepClone();
        foreach (var (key, value) in etsy)
            if (!ListingFields.IsTracked(key) || !result.ContainsKey(key))
                result[key] = value?.DeepClone();
        return result;
    }

    public JsonNode? Local(string key) => Draft.TryGetPropertyValue(key, out var v) ? v : Base[key];

    public JsonObject LocalState()
    {
        var result = (JsonObject)Base.DeepClone();
        foreach (var (key, value) in Draft) result[key] = value?.DeepClone();
        return result;
    }

    /// <summary>Tracked fields changed on Etsy since the base. Pushing is blocked while there are any.</summary>
    public IReadOnlyList<string> DriftFields =>
        ListingFields.All.Where(f => f.Tracked && !JsonNode.DeepEquals(Etsy[f.Key], Base[f.Key])).Select(f => f.Key).ToList();

    /// <summary>Editable fields whose local value differs from Etsy: what a push would change.</summary>
    public IReadOnlyList<string> PendingFields =>
        ListingFields.All.Where(f => f.Editable && !JsonNode.DeepEquals(Local(f.Key), Etsy[f.Key])).Select(f => f.Key).ToList();

    public bool HasDraft => Draft.Count > 0;

    /// <summary>Sets a local value; setting it back to the agreed value removes the edit.</summary>
    public void Set(string key, JsonNode? value)
    {
        if (!ListingFields.IsEditable(key)) throw new ArgumentException($"'{key}' can't be edited.", nameof(key));
        if (JsonNode.DeepEquals(value, Base[key])) Draft.Remove(key);
        else Draft[key] = value?.DeepClone();
    }

    public void Discard(string key) => Draft.Remove(key);

    /// <summary>Drift resolution "keep mine": acknowledge Etsy's value, keep the local one, so the next push restores it.</summary>
    public void KeepMine(string key)
    {
        var local = Local(key)?.DeepClone();
        Base[key] = Etsy[key]?.DeepClone();
        if (JsonNode.DeepEquals(local, Base[key])) Draft.Remove(key);
        else Draft[key] = local;
    }

    /// <summary>Drift resolution "take Etsy's": Etsy's value becomes the local one and any local edit of it is dropped.</summary>
    public void TakeEtsy(string key)
    {
        Base[key] = Etsy[key]?.DeepClone();
        Draft.Remove(key);
    }

    /// <summary>
    /// After a push and a fresh read: everything Etsy now holds is agreed, and draft entries Etsy accepted are done.
    /// Returns the fields that still differ (Etsy rejected or adjusted them); they stay in the draft.
    /// </summary>
    public IReadOnlyList<string> AcceptPushed(JsonObject newEtsy)
    {
        var notApplied = new List<string>();
        foreach (var key in Draft.Select(p => p.Key).ToList())
        {
            if (JsonNode.DeepEquals(Draft[key], newEtsy[key])) Draft.Remove(key);
            else notApplied.Add(key);
        }
        Etsy.Clear();
        foreach (var (key, value) in newEtsy) Etsy[key] = value?.DeepClone();
        Base.Clear();
        foreach (var (key, value) in newEtsy) Base[key] = value?.DeepClone();
        return notApplied;
    }
}
