using System.Net.Http.Json;
using System.Text.Json;
using EtsyShopManager.Etsy.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace EtsyShopManager.Etsy;

/// <summary>OAuth 2.0 authorization code + PKCE flow against the existing Etsy developer app.</summary>
public sealed class EtsyOAuthClient(
    IHttpClientFactory httpClientFactory,
    IOptions<EtsyOptions> options,
    IEtsyCredentialsProvider credentials,
    TimeProvider time)
{
    public const string HttpClientName = "Etsy";

    public async Task<string> BuildAuthorizeUrlAsync(string redirectUri, string state, string codeChallenge,
        CancellationToken ct = default)
    {
        var creds = await credentials.GetAsync(ct) ?? throw new EtsyNotConfiguredException();
        return QueryHelpers.AddQueryString(options.Value.AuthorizeUrl, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = creds.Keystring,
            ["redirect_uri"] = redirectUri,
            ["scope"] = options.Value.Scopes,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });
    }

    public async Task<EtsyTokenSet> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri,
        CancellationToken ct = default)
    {
        var creds = await credentials.GetAsync(ct) ?? throw new EtsyNotConfiguredException();
        var token = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = creds.Keystring,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
        }, ct);
        return ToTokenSet(token, options.Value.Scopes);
    }

    public async Task<EtsyTokenSet> RefreshAsync(string refreshToken, string scopes, CancellationToken ct = default)
    {
        var creds = await credentials.GetAsync(ct) ?? throw new EtsyNotConfiguredException();
        var token = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = creds.Keystring,
            ["refresh_token"] = refreshToken,
        }, ct);
        return ToTokenSet(token, scopes);
    }

    private async Task<EtsyTokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            EtsyErrorResponse? err = null;
            try { err = JsonSerializer.Deserialize<EtsyErrorResponse>(body, EtsyJson.Options); }
            catch (JsonException) { }
            if ((int)response.StatusCode == 429)
                throw new EtsyRateLimitedException(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            throw new EtsyOAuthException(response.StatusCode, err?.Error, err?.ErrorDescription, body);
        }

        var token = JsonSerializer.Deserialize<EtsyTokenResponse>(body, EtsyJson.Options);
        if (token is null || string.IsNullOrEmpty(token.AccessToken) || string.IsNullOrEmpty(token.RefreshToken))
            throw new EtsyOAuthException(response.StatusCode, "invalid_response", "Token response was missing fields", body);
        return token;
    }

    private EtsyTokenSet ToTokenSet(EtsyTokenResponse t, string scopes)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return new EtsyTokenSet
        {
            AccessToken = t.AccessToken,
            RefreshToken = t.RefreshToken,
            AccessTokenExpiresAtUtc = now.AddSeconds(t.ExpiresIn > 0 ? t.ExpiresIn : 3600),
            RefreshTokenIssuedAtUtc = now,
            Scopes = scopes,
        };
    }
}
