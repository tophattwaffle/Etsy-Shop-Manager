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

/// <summary>
/// One write request the app sent to Etsy's API and what came back, as recorded by the write journal. Bodies are the
/// form/JSON as sent (file uploads are summarised, never stored) and are cut to a few KB.
/// </summary>
public sealed record EtsyWriteRecord
{
    public required DateTime AtUtc { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string? RequestBody { get; init; }

    /// <summary>Null when no response arrived (network error, timeout).</summary>
    public int? StatusCode { get; init; }
    public string? ResponseBody { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }

    /// <summary>What made the call, e.g. "job 229 etsy.listing.push" (see <see cref="EtsyCallContext"/>).</summary>
    public string? Source { get; init; }
}

/// <summary>
/// Keeps a record of every write sent to Etsy (POST, PUT, PATCH, DELETE on the application API). The host stores it;
/// recording must never make a write fail.
/// </summary>
public interface IEtsyWriteJournal
{
    Task RecordAsync(EtsyWriteRecord record, CancellationToken ct = default);
}

internal sealed class NullEtsyWriteJournal : IEtsyWriteJournal
{
    public Task RecordAsync(EtsyWriteRecord record, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Names what is calling Etsy on the current async flow (a job, a page), for the write journal.</summary>
public static class EtsyCallContext
{
    private static readonly AsyncLocal<string?> CurrentSource = new();

    public static string? Source => CurrentSource.Value;

    /// <summary>Sets the source until the returned scope is disposed.</summary>
    public static IDisposable Begin(string source)
    {
        var previous = CurrentSource.Value;
        CurrentSource.Value = source;
        return new Scope(() => CurrentSource.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
