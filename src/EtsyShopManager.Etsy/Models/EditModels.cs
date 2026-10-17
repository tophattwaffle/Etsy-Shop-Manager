using System.Text.Json.Serialization;

namespace EtsyShopManager.Etsy.Models;

/// <summary>Body of updateListingInventory. Etsy replaces the listing's whole inventory with it.</summary>
public sealed class EtsyInventoryUpdate
{
    [JsonPropertyName("products")] public List<EtsyProductUpdate> Products { get; set; } = [];
    [JsonPropertyName("price_on_property")] public List<long> PriceOnProperty { get; set; } = [];
    [JsonPropertyName("quantity_on_property")] public List<long> QuantityOnProperty { get; set; } = [];
    [JsonPropertyName("sku_on_property")] public List<long> SkuOnProperty { get; set; } = [];
    [JsonPropertyName("readiness_state_on_property")] public List<long> ReadinessStateOnProperty { get; set; } = [];
}

public sealed class EtsyProductUpdate
{
    [JsonPropertyName("sku")] public string Sku { get; set; } = "";
    [JsonPropertyName("property_values")] public List<EtsyPropertyValueUpdate> PropertyValues { get; set; } = [];
    [JsonPropertyName("offerings")] public List<EtsyOfferingUpdate> Offerings { get; set; } = [];
}

public sealed class EtsyPropertyValueUpdate
{
    [JsonPropertyName("property_id")] public long PropertyId { get; set; }
    [JsonPropertyName("value_ids")] public List<long> ValueIds { get; set; } = [];
    [JsonPropertyName("scale_id")] public long? ScaleId { get; set; }
    [JsonPropertyName("property_name")] public string? PropertyName { get; set; }
    [JsonPropertyName("values")] public List<string> Values { get; set; } = [];
}

public sealed class EtsyOfferingUpdate
{
    /// <summary>In currency units (e.g. 19.99), not Etsy's amount/divisor.</summary>
    [JsonPropertyName("price")] public decimal Price { get; set; }
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("is_enabled")] public bool IsEnabled { get; set; }
    [JsonPropertyName("readiness_state_id")] public long? ReadinessStateId { get; set; }
}
