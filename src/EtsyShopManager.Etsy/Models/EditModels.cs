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

/// <summary>Body of updateListingPersonalization. Etsy replaces all of the listing's questions with these.</summary>
public sealed class EtsyPersonalizationUpdate
{
    [JsonPropertyName("personalization_questions")] public List<EtsyPersonalizationQuestionUpdate> PersonalizationQuestions { get; set; } = [];
}

/// <summary>A question to write. Ids are given for questions (and options) that already exist, left out for new ones.</summary>
public sealed class EtsyPersonalizationQuestionUpdate
{
    [JsonPropertyName("question_id")] public long? QuestionId { get; set; }
    [JsonPropertyName("question_text")] public string QuestionText { get; set; } = "";
    [JsonPropertyName("instructions")] public string? Instructions { get; set; }
    [JsonPropertyName("question_type")] public string QuestionType { get; set; } = "text_input";
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("max_allowed_characters")] public long? MaxAllowedCharacters { get; set; }
    [JsonPropertyName("max_allowed_files")] public long? MaxAllowedFiles { get; set; }
    [JsonPropertyName("options")] public List<EtsyPersonalizationOptionUpdate>? Options { get; set; }
    /// <summary>In the shop's currency (a number, e.g. 2.5), unlike the money object Etsy returns.</summary>
    [JsonPropertyName("add_on_price")] public decimal? AddOnPrice { get; set; }
}

public sealed class EtsyPersonalizationOptionUpdate
{
    [JsonPropertyName("option_id")] public long? OptionId { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";
}
