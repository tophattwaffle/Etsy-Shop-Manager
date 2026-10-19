using System.Text.Json;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;

namespace EtsyShopManager.Web.Modules.Navigation;

/// <summary>One line of the sidebar: a page, or a group heading (<see cref="Group"/>) with its pages.</summary>
public sealed record SidebarEntry(string Key, NavItem? Item, string? Group, IReadOnlyList<SidebarEntry> Children)
{
    public string Title => Item?.Title ?? Group ?? "";
}

/// <summary>
/// The sidebar arranged in a user's own order. The order is a list of keys (a page's "/" + href, a group's
/// "group:" + name); pages and groups can move among their neighbours, and a group's pages within the group.
/// </summary>
public static class SidebarLayout
{
    public static string KeyOf(NavItem item) => "/" + item.Href;

    public static string GroupKey(string group) => "group:" + group;

    /// <summary>
    /// The sidebar for <paramref name="items"/> (in their default order), arranged by <paramref name="saved"/>.
    /// A group sits where its first page would. A page the saved order doesn't mention (added since it was saved) goes
    /// after the page it follows by default.
    /// </summary>
    public static List<SidebarEntry> Build(IEnumerable<NavItem> items, IReadOnlyList<string>? saved = null)
    {
        var list = items.ToList();
        var position = new Dictionary<string, int>();
        for (var i = 0; i < (saved?.Count ?? 0); i++) position.TryAdd(saved![i], i);

        var top = new List<SidebarEntry>();
        var groups = new HashSet<string>();
        foreach (var item in list)
        {
            if (item.Group is null)
            {
                top.Add(Page(item));
            }
            else if (groups.Add(item.Group))
            {
                var group = item.Group;
                var pages = list.Where(i => i.Group == group).Select(Page).ToList();
                top.Add(new SidebarEntry(GroupKey(group), null, group, Arrange(pages, position)));
            }
        }
        return Arrange(top, position);
    }

    /// <summary>The keys to save for a sidebar: each entry followed by its group's pages.</summary>
    public static List<string> Keys(IEnumerable<SidebarEntry> entries) =>
        entries.SelectMany(e => e.Children.Select(c => c.Key).Prepend(e.Key)).ToList();

    /// <summary>Moves an entry up (negative) or down among its neighbours, stopping at the ends.</summary>
    public static List<SidebarEntry> MoveBy(IReadOnlyList<SidebarEntry> entries, string key, int by) =>
        Rearrange(entries, key, siblings => Math.Clamp(siblings.FindIndex(e => e.Key == key) + by, 0, siblings.Count - 1));

    /// <summary>
    /// Puts an entry in the place of <paramref name="targetKey"/> (as when dropped on it). Nothing changes when the two
    /// aren't neighbours, such as a page dropped on a page in another group.
    /// </summary>
    public static List<SidebarEntry> MoveTo(IReadOnlyList<SidebarEntry> entries, string key, string targetKey) =>
        Rearrange(entries, key, siblings => siblings.FindIndex(e => e.Key == targetKey));

    /// <summary>The group an entry sits in, or null for the top level.</summary>
    public static string? ParentOf(IEnumerable<SidebarEntry> entries, string key) =>
        entries.FirstOrDefault(e => e.Children.Any(c => c.Key == key))?.Key;

    private static SidebarEntry Page(NavItem item) => new(KeyOf(item), item, null, []);

    private static List<SidebarEntry> Arrange(List<SidebarEntry> defaults, Dictionary<string, int> position)
    {
        if (position.Count == 0) return defaults;
        var result = defaults.Where(e => position.ContainsKey(e.Key)).OrderBy(e => position[e.Key]).ToList();
        for (var i = 0; i < defaults.Count; i++)
        {
            if (position.ContainsKey(defaults[i].Key)) continue;
            var at = i == 0 ? 0 : result.FindIndex(e => e.Key == defaults[i - 1].Key) + 1;
            result.Insert(at, defaults[i]);
        }
        return result;
    }

    // Moves `key` to the index `target` picks in its own list of neighbours; a target of -1 leaves things as they are.
    private static List<SidebarEntry> Rearrange(IReadOnlyList<SidebarEntry> entries, string key, Func<List<SidebarEntry>, int> target)
    {
        var top = entries.ToList();
        if (top.FindIndex(e => e.Key == key) >= 0) return Moved(top, key, target);

        var g = top.FindIndex(e => e.Children.Any(c => c.Key == key));
        if (g >= 0) top[g] = top[g] with { Children = Moved(top[g].Children.ToList(), key, target) };
        return top;
    }

    private static List<SidebarEntry> Moved(List<SidebarEntry> siblings, string key, Func<List<SidebarEntry>, int> target)
    {
        var from = siblings.FindIndex(e => e.Key == key);
        var to = target(siblings);
        if (from < 0 || to < 0 || from == to) return siblings;
        var entry = siblings[from];
        siblings.RemoveAt(from);
        siblings.Insert(to, entry);
        return siblings;
    }
}

/// <summary>Each user's sidebar order, kept as an app setting ("Sidebar.Order.{userId}"), so no schema change.</summary>
public sealed class SidebarOrderService(ISettingsStore settings)
{
    public static string SettingKey(string userId) => $"Sidebar.Order.{userId}";

    /// <summary>The user's saved order, or null for the default order (nothing saved, or it can't be read).</summary>
    public async Task<List<string>?> GetAsync(string userId, CancellationToken ct = default)
    {
        if (await settings.GetAsync(SettingKey(userId), ct) is not { Length: > 0 } json) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SaveAsync(string userId, IEnumerable<string> keys, CancellationToken ct = default) =>
        settings.SetAsync(SettingKey(userId), JsonSerializer.Serialize(keys.Distinct().ToList()), updatedBy: userId, ct: ct);

    /// <summary>Back to the default order.</summary>
    public Task ResetAsync(string userId, CancellationToken ct = default) =>
        settings.SetAsync(SettingKey(userId), null, updatedBy: userId, ct: ct);
}

/// <summary>Registers the sidebar order store.</summary>
public sealed class NavigationModule : IModule
{
    public string Name => "Navigation";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<SidebarOrderService>();
}
