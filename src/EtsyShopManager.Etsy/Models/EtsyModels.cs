using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtsyShopManager.Etsy.Models;

public static class EtsyJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

public sealed class EtsyTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
}

public sealed class EtsyErrorResponse
{
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

/// <summary>GET /v3/application/users/me</summary>
public sealed class EtsyMe
{
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("shop_id")] public long? ShopId { get; set; }
}

/// <summary>GET /v3/application/users/{user_id} (the names; primary_email needs a permission Etsy grants case by case).</summary>
public sealed class EtsyUser
{
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
    [JsonPropertyName("last_name")] public string? LastName { get; set; }
}

/// <summary>GET /v3/application/shops/{shop_id} (subset used so far).</summary>
public sealed class EtsyShop
{
    [JsonPropertyName("shop_id")] public long ShopId { get; set; }
    [JsonPropertyName("shop_name")] public string ShopName { get; set; } = "";
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("currency_code")] public string? CurrencyCode { get; set; }
    [JsonPropertyName("icon_url_fullxfull")] public string? IconUrl { get; set; }
    [JsonPropertyName("listing_active_count")] public int? ListingActiveCount { get; set; }
    [JsonPropertyName("is_vacation")] public bool? IsVacation { get; set; }
    [JsonPropertyName("transaction_sold_count")] public int? TransactionSoldCount { get; set; }
}
