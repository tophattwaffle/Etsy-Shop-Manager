namespace EtsyShopManager.Web.Components.Shared;

/// <summary>
/// Manages shift-click range selection state for multi-select lists and tables.
/// </summary>
/// <typeparam name="TKey">The type of unique row/item identifier.</typeparam>
public sealed class RangeSelection<TKey>
{
    private bool _hasAnchor;
    private TKey? _anchorKey;

    public TKey? AnchorKey => _hasAnchor ? _anchorKey : default;
    public bool AnchorState { get; private set; }
    public bool HasAnchor => _hasAnchor;

    public void Reset()
    {
        _hasAnchor = false;
        _anchorKey = default;
        AnchorState = false;
    }

    public void ClearAnchor() => Reset();

    public void HandleClick(
        TKey clickedKey,
        bool shiftKey,
        IReadOnlyList<TKey> visibleKeys,
        ISet<TKey> selected,
        Func<TKey, bool>? canSelect = null)
    {
        var comparer = EqualityComparer<TKey>.Default;
        int anchorIndex = -1;
        if (_hasAnchor)
        {
            for (int i = 0; i < visibleKeys.Count; i++)
            {
                if (comparer.Equals(visibleKeys[i], _anchorKey!))
                {
                    anchorIndex = i;
                    break;
                }
            }
        }

        if (!shiftKey || !_hasAnchor || anchorIndex == -1)
        {
            if (canSelect != null && !canSelect(clickedKey))
                return;

            bool newState = !selected.Contains(clickedKey);
            if (newState)
                selected.Add(clickedKey);
            else
                selected.Remove(clickedKey);

            _anchorKey = clickedKey;
            _hasAnchor = true;
            AnchorState = newState;
            return;
        }

        int currentIndex = -1;
        for (int i = 0; i < visibleKeys.Count; i++)
        {
            if (comparer.Equals(visibleKeys[i], clickedKey))
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex == -1)
        {
            if (canSelect != null && !canSelect(clickedKey))
                return;

            bool newState = !selected.Contains(clickedKey);
            if (newState)
                selected.Add(clickedKey);
            else
                selected.Remove(clickedKey);

            _anchorKey = clickedKey;
            _hasAnchor = true;
            AnchorState = newState;
            return;
        }

        int start = Math.Min(anchorIndex, currentIndex);
        int end = Math.Max(anchorIndex, currentIndex);
        for (int i = start; i <= end; i++)
        {
            var key = visibleKeys[i];
            if (canSelect != null && !canSelect(key))
                continue;

            if (AnchorState)
                selected.Add(key);
            else
                selected.Remove(key);
        }
    }
}
