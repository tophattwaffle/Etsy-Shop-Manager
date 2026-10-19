namespace EtsyShopManager.Web.Components.Shared;

/// <summary>
/// A form's last saved values, so the form knows it has unsaved changes (<see cref="SaveButtons"/> then highlights Save
/// and shows Cancel) and Cancel can put the saved values back. <typeparamref name="T"/> is a record of the form's values
/// and is compared by value, so give it strings, numbers and bools (normalise null text to "" when reading, or clearing
/// a field reads as a change).
/// </summary>
public sealed class SavedValues<T>
{
    private readonly Func<T> _read;
    private readonly Action<T> _restore;
    private T _saved;

    /// <summary>Starts with the form's current values as the saved ones.</summary>
    /// <param name="read">The form's values as they are now.</param>
    /// <param name="restore">Puts values back into the form's fields.</param>
    public SavedValues(Func<T> read, Action<T> restore)
    {
        (_read, _restore) = (read, restore);
        _saved = read();
    }

    /// <summary>The form's values differ from the last saved ones.</summary>
    public bool Changed => !EqualityComparer<T>.Default.Equals(_read(), _saved);

    /// <summary>The form's current values are now the saved ones (call after saving or loading).</summary>
    public void Accept() => _saved = _read();

    /// <summary>Puts the last saved values back into the form (Cancel).</summary>
    public void Revert() => _restore(_saved);
}
