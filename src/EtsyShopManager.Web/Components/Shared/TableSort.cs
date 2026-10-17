using System.Collections;

namespace EtsyShopManager.Web.Components.Shared;

/// <summary>The sort state of a table, as <see cref="SortHeader"/> sees it.</summary>
public interface ITableSort
{
    /// <summary>The column sorted by, or null for the table's natural order.</summary>
    string? Column { get; }

    bool Descending { get; }

    /// <summary>A header was clicked: sort by that column ascending, or flip the direction if it already is.</summary>
    void Toggle(string column);
}

/// <summary>
/// Header-click sorting for a table whose rows are all loaded. Each column key maps to the value it sorts by (numbers,
/// dates and money by value; text ignoring case); nulls come first ascending. Until a header is clicked, rows keep the
/// order they were loaded in.
/// <code>
/// private readonly TableSort&lt;ApiKey&gt; _sort = new() { ["name"] = k =&gt; k.Name, ["created"] = k =&gt; k.CreatedAtUtc };
/// &lt;SortHeader Sort="_sort" Column="name" Changed="StateHasChanged"&gt;Name&lt;/SortHeader&gt;
/// @foreach (var k in _sort.Apply(_keys)) { … }
/// </code>
/// </summary>
public sealed class TableSort<T> : ITableSort, IEnumerable<KeyValuePair<string, Func<T, object?>>>
{
    private readonly Dictionary<string, Func<T, object?>> _keys = [];

    public string? Column { get; private set; }
    public bool Descending { get; private set; }

    public Func<T, object?> this[string column]
    {
        set => _keys[column] = value;
    }

    public void Add(string column, Func<T, object?> key) => _keys[column] = key;

    public void Toggle(string column)
    {
        if (!_keys.ContainsKey(column)) throw new ArgumentException($"Unknown sort column '{column}'.", nameof(column));
        Descending = Column == column && !Descending;
        Column = column;
    }

    /// <summary>Sorts by <paramref name="column"/>, e.g. a page's default.</summary>
    public void Set(string column, bool descending)
    {
        Toggle(column);
        Descending = descending;
    }

    public IEnumerable<T> Apply(IEnumerable<T> rows)
    {
        if (Column is null || !_keys.TryGetValue(Column, out var key)) return rows;
        return Descending ? rows.OrderByDescending(key, ValueComparer.Instance) : rows.OrderBy(key, ValueComparer.Instance);
    }

    public IEnumerator<KeyValuePair<string, Func<T, object?>>> GetEnumerator() => _keys.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Sort state for a paged table that the server sorts (so the order covers every page, not just the visible one):
/// the page turns <see cref="Column"/> and <see cref="Descending"/> into its query and reloads in the header's
/// <c>Changed</c> callback.
/// </summary>
public sealed class ColumnSort(params string[] columns) : ITableSort
{
    private readonly HashSet<string> _columns = [.. columns];

    public string? Column { get; private set; }
    public bool Descending { get; private set; }

    public void Toggle(string column)
    {
        if (!_columns.Contains(column)) throw new ArgumentException($"Unknown sort column '{column}'.", nameof(column));
        Descending = Column == column && !Descending;
        Column = column;
    }

    public void Set(string? column, bool descending)
    {
        if (column is not null && !_columns.Contains(column)) throw new ArgumentException($"Unknown sort column '{column}'.", nameof(column));
        (Column, Descending) = (column, descending);
    }
}

public static class SortQueryExtensions
{
    /// <summary>OrderBy or OrderByDescending, for server-side sorts driven by a <see cref="ColumnSort"/>.</summary>
    public static IOrderedQueryable<T> OrderBy<T, TKey>(this IQueryable<T> query,
        System.Linq.Expressions.Expression<Func<T, TKey>> key, bool descending) =>
        descending ? query.OrderByDescending(key) : query.OrderBy(key);
}

/// <summary>Compares sort values: text ignoring case, everything else by its natural order, nulls first.</summary>
internal sealed class ValueComparer : IComparer<object?>
{
    public static readonly ValueComparer Instance = new();

    public int Compare(object? x, object? y) => (x, y) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        (string a, string b) => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase),
        _ => Comparer<object>.Default.Compare(x, y),
    };
}
