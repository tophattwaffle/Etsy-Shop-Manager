namespace EtsyShopManager.Web.Modules.Shops;

/// <summary>
/// Per-shop default profiles and attributes, pre-filled when creating a new listing or cloning into the shop.
/// </summary>
public sealed record ShopDefaults(
    long? ShippingProfileId,
    long? ReadinessStateId,
    long? ReturnPolicyId,
    long? ShopSectionId,
    string? WhoMade = "i_did",
    string? WhenMade = "made_to_order",
    long? TaxonomyId = null);
