namespace EtsyShopManager.Core.Domain;

/// <summary>An Etsy shop connected to this app through OAuth.</summary>
public class Shop
{
    public int Id { get; set; }

    /// <summary>Etsy's shop_id.</summary>
    public long EtsyShopId { get; set; }

    /// <summary>Etsy user_id of the account that owns the shop (and granted the token).</summary>
    public long EtsyUserId { get; set; }

    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? Url { get; set; }
    public string? CurrencyCode { get; set; }
    public string? IconUrl { get; set; }

    /// <summary>Etsy's count of the shop's sales (getShop transaction_sold_count), shown on gift slips as Etsy's do.</summary>
    public int? TransactionSoldCount { get; set; }

    /// <summary>False after the shop is disconnected. Local data is kept.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime ConnectedAtUtc { get; set; }
    public DateTime? LastSyncedAtUtc { get; set; }

    public ShopToken? Token { get; set; }
}

/// <summary>OAuth tokens for a shop. Token values are encrypted with ASP.NET Data Protection.</summary>
public class ShopToken
{
    public int ShopId { get; set; }
    public Shop Shop { get; set; } = null!;

    public string EncryptedAccessToken { get; set; } = "";
    public string EncryptedRefreshToken { get; set; } = "";
    public DateTime AccessTokenExpiresAtUtc { get; set; }

    /// <summary>When the current refresh token was issued (Etsy refresh tokens last about 90 days).</summary>
    public DateTime RefreshTokenIssuedAtUtc { get; set; }

    public string Scopes { get; set; } = "";
    public DateTime? LastRefreshedAtUtc { get; set; }

    /// <summary>Set when Etsy rejects the refresh token. The shop must be reconnected.</summary>
    public bool NeedsReauth { get; set; }
    public string? LastError { get; set; }
}
