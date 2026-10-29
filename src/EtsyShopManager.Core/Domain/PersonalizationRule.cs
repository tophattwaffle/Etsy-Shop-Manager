namespace EtsyShopManager.Core.Domain;

public enum PersonalizationRuleType
{
    ColorCount = 1,
    WordList = 2,
    NotEmpty = 3,
    Regex = 4,
}

/// <summary>
/// A named personalization verification rule shared across listings (e.g. "Select 2 Filaments", "Switch Choice").
/// </summary>
public class PersonalizationRule
{
    public int Id { get; set; }

    /// <summary>Unique name displayed in UI and alert messages.</summary>
    public string Name { get; set; } = "";

    /// <summary>Type of rule evaluation.</summary>
    public PersonalizationRuleType RuleType { get; set; }

    /// <summary>Color count: minimum expected colors per item unit (null if unbounded).</summary>
    public int? MinColors { get; set; }

    /// <summary>Color count: maximum expected colors per item unit (null if unbounded).</summary>
    public int? MaxColors { get; set; }

    /// <summary>
    /// Word list: newline-separated word options, with '/' separating synonyms for each choice.
    /// E.g.: "linear / red / yellow\ntactile / brown\nclicky / blue"
    /// </summary>
    public string? WordList { get; set; }

    /// <summary>Regular expression pattern for Regex rule type.</summary>
    public string? RegexPattern { get; set; }

    /// <summary>
    /// Optional variation filter: when set, the rule only evaluates if the item's chosen variations match one of the
    /// options (separated by '/' or newlines, e.g. "Dual Color" or "Single Color / Choose Color").
    /// </summary>
    public string? VariationFilter { get; set; }

    /// <summary>Optional custom alert message override (defaults to rule engine explanation if null).</summary>
    public string? CustomAlertMessage { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Listings linked to this rule.</summary>
    public List<ListingPersonalizationRule> Listings { get; set; } = [];
}

/// <summary>
/// Join entity linking a listing to a personalization verification rule (many-to-many).
/// </summary>
public class ListingPersonalizationRule
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;

    public int PersonalizationRuleId { get; set; }
    public PersonalizationRule PersonalizationRule { get; set; } = null!;

    public DateTime LinkedAtUtc { get; set; }
}
