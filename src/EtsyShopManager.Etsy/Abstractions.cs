namespace EtsyShopManager.Etsy;

public sealed record EtsyCredentials(string Keystring, string SharedSecret)
{
    /// <summary>Etsy v3 requires "keystring:shared_secret" in the x-api-key header.</summary>
    public string ApiKeyHeaderValue => $"{Keystring}:{SharedSecret}";
}

/// <summary>Supplies the Etsy app keystring and shared secret (stored encrypted by the host).</summary>
public interface IEtsyCredentialsProvider
{
    Task<EtsyCredentials?> GetAsync(CancellationToken ct = default);
}

public sealed record EtsyTokenSet
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTime AccessTokenExpiresAtUtc { get; init; }
    public required DateTime RefreshTokenIssuedAtUtc { get; init; }
    public string Scopes { get; init; } = "";
    public bool NeedsReauth { get; init; }

    /// <summary>The numeric prefix of an Etsy access token is the Etsy user_id that granted it.</summary>
    public long? EtsyUserId => EtsyTokenParser.TryGetUserId(AccessToken);
}

/// <summary>Persists tokens per local shop id. The host implements it (encrypted in SQLite).</summary>
public interface IEtsyTokenStore
{
    Task<EtsyTokenSet?> GetAsync(int shopId, CancellationToken ct = default);
    Task SaveAsync(int shopId, EtsyTokenSet tokens, CancellationToken ct = default);
    Task MarkReauthRequiredAsync(int shopId, string reason, CancellationToken ct = default);
}

public static class EtsyTokenParser
{
    public static long? TryGetUserId(string? accessToken)
    {
        if (string.IsNullOrEmpty(accessToken)) return null;
        var dot = accessToken.IndexOf('.');
        return dot > 0 && long.TryParse(accessToken.AsSpan(0, dot), out var id) ? id : null;
    }
}
