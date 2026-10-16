using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EtsyShopManager.Etsy.Models;

namespace EtsyShopManager.Etsy;

/// <summary>How a call authenticates: as a connected shop (token looked up and refreshed) or with a raw token.</summary>
public readonly record struct EtsyAuth(int? ShopId, string? AccessToken)
{
    public static EtsyAuth ForShop(int shopId) => new(shopId, null);
    public static EtsyAuth WithToken(string accessToken) => new(null, accessToken);
}

/// <summary>
/// Typed Etsy Open API v3 client. Calls go through the "Etsy" HttpClient (rate limiter, retry, x-api-key).
/// Methods are hand-written from Etsy's OpenAPI spec; Phase 1 reads live in EtsyApiClient.Listings.cs.
/// </summary>
public sealed partial class EtsyApiClient(IHttpClientFactory httpClientFactory, EtsyTokenService tokens)
{
    public Task<EtsyMe> GetMeAsync(EtsyAuth auth, CancellationToken ct = default) =>
        GetAsync<EtsyMe>("/v3/application/users/me", auth, ct);

    public Task<EtsyShop> GetShopAsync(long etsyShopId, EtsyAuth auth, CancellationToken ct = default) =>
        GetAsync<EtsyShop>($"/v3/application/shops/{etsyShopId}", auth, ct);

    public async Task<T> GetAsync<T>(string relativePath, EtsyAuth auth, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, relativePath), auth, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, EtsyJson.Options, ct)
               ?? throw new EtsyApiException(response.StatusCode, "empty", null, $"Empty response from {relativePath}");
    }

    /// <summary>
    /// Sends a request and throws <see cref="EtsyApiException"/> on failure. For shop auth a 401 triggers one
    /// forced token refresh and a single retry. The factory is called again for the retry.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, EtsyAuth auth,
        CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(EtsyOAuthClient.HttpClientName);

        var response = await SendOnceAsync(client, requestFactory(), await ResolveTokenAsync(auth, ct), ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && auth.ShopId is { } shopId)
        {
            response.Dispose();
            var refreshed = await tokens.RefreshAsync(shopId, force: true, ct);
            response = await SendOnceAsync(client, requestFactory(), refreshed.AccessToken, ct);
        }

        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new EtsyRateLimitedException(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));

            var body = await response.Content.ReadAsStringAsync(ct);
            string? error = null;
            try { error = JsonSerializer.Deserialize<EtsyErrorResponse>(body, EtsyJson.Options)?.Error; }
            catch (JsonException) { }
            throw new EtsyApiException(response.StatusCode, error, body,
                $"Etsy API {(int)response.StatusCode} for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: {error ?? body}");
        }
    }

    private async Task<string> ResolveTokenAsync(EtsyAuth auth, CancellationToken ct) =>
        auth.AccessToken ?? await tokens.GetAccessTokenAsync(
            auth.ShopId ?? throw new ArgumentException("EtsyAuth needs a shop id or an access token."), ct);

    private static async Task<HttpResponseMessage> SendOnceAsync(HttpClient client, HttpRequestMessage request,
        string accessToken, CancellationToken ct)
    {
        using (request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
    }
}
