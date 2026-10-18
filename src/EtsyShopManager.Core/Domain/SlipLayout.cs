namespace EtsyShopManager.Core.Domain;

/// <summary>Which printout a slip layout is for.</summary>
public enum SlipKind
{
    /// <summary>The packing slip for an ordinary order (with prices).</summary>
    Packing = 0,

    /// <summary>The gift slip for a gift order (no prices, the gift message).</summary>
    Gift = 1,
}

/// <summary>
/// A packing slip layout edited on the Packing slips page: a Liquid template (HTML with placeholders) and its CSS. Each
/// shop prints the built-in layout of each kind until it is given one of these.
/// </summary>
public class SlipLayout
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public SlipKind Kind { get; set; }
    public string Html { get; set; } = "";
    public string Css { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}
