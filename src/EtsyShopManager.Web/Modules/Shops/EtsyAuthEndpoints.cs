using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Modules;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace EtsyShopManager.Web.Modules.Shops;

/// <summary>
/// Browser-side OAuth for connecting a shop:
///   GET /auth/etsy/connect   → stores state + PKCE verifier in an encrypted cookie, redirects to Etsy
///   GET /auth/etsy/callback  → validates state, exchanges the code, identifies the shop, stores tokens
/// The redirect happens in the browser, so the callback URL only has to be reachable from the browser (LAN/VPN).
/// </summary>
public static class EtsyAuthEndpoints
{
    private const string CookieName = "esm.etsy.oauth";
    private const string ProtectorPurpose = "EtsyShopManager.OAuthState.v1";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

    private sealed record OAuthState(string State, string Verifier, string UserId, DateTime CreatedUtc, string ReturnUrl);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth/etsy")
            .RequireAuthorization(policy => policy.RequireRole(Roles.Admin))
            .ExcludeFromDescription();

        group.MapGet("/connect", ConnectAsync);
        group.MapGet("/callback", CallbackAsync);
    }

    private static async Task<IResult> ConnectAsync(
        HttpContext http,
        ISettingsStore settings,
        IEtsyCredentialsProvider credentials,
        EtsyOAuthClient oauth,
        IDataProtectionProvider dataProtection,
        TimeProvider time,
        string? returnUrl)
    {
        var back = UrlHelpers.SafeLocal(returnUrl, "/shops");

        var baseUrl = await settings.GetAsync(SettingKeys.BaseUrl, http.RequestAborted);
        if (string.IsNullOrEmpty(baseUrl))
            return Results.LocalRedirect(WithQuery(back, "etsyError", "Set the base URL first (Admin → Settings)."));
        if (await credentials.GetAsync(http.RequestAborted) is null)
            return Results.LocalRedirect(WithQuery(back, "etsyError", "Enter the Etsy keystring and shared secret first."));

        // The state cookie must be set on the same host Etsy redirects back to.
        var baseUri = new Uri(baseUrl);
        if (!string.Equals(http.Request.Host.Value, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
            return Results.Redirect($"{baseUrl}/auth/etsy/connect?returnUrl={Uri.EscapeDataString(back)}");

        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var payload = new OAuthState(Pkce.CreateState(), Pkce.CreateVerifier(), userId, time.GetUtcNow().UtcDateTime, back);
        var protector = dataProtection.CreateProtector(ProtectorPurpose);

        http.Response.Cookies.Append(CookieName, protector.Protect(JsonSerializer.Serialize(payload)), new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax, // sent on Etsy's top-level redirect back to us
            Path = "/auth/etsy",
            MaxAge = StateLifetime,
            IsEssential = true,
        });

        var redirectUri = UrlHelpers.EtsyRedirectUri(baseUrl);
        var url = await oauth.BuildAuthorizeUrlAsync(redirectUri, payload.State, Pkce.CreateChallenge(payload.Verifier), http.RequestAborted);
        return Results.Redirect(url);
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext http,
        ISettingsStore settings,
        EtsyOAuthClient oauth,
        EtsyApiClient etsy,
        ShopService shops,
        IDataProtectionProvider dataProtection,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        string? code,
        string? state,
        string? error,
        string? error_description)
    {
        var logger = loggerFactory.CreateLogger(typeof(EtsyAuthEndpoints));
        var ct = http.RequestAborted;

        OAuthState? saved = null;
        if (http.Request.Cookies.TryGetValue(CookieName, out var cookie))
        {
            try
            {
                saved = JsonSerializer.Deserialize<OAuthState>(dataProtection.CreateProtector(ProtectorPurpose).Unprotect(cookie));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                logger.LogWarning(ex, "Unreadable Etsy OAuth state cookie");
            }
        }
        http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/auth/etsy" });

        var back = saved?.ReturnUrl ?? "/shops";

        if (saved is null)
            return Fail("The sign-in session expired or was started in another browser. Please try again.");
        if (string.IsNullOrEmpty(state) || !FixedEquals(state, saved.State))
            return Fail("The Etsy response did not match this sign-in attempt (state mismatch). Please try again.");
        if (time.GetUtcNow().UtcDateTime - saved.CreatedUtc > StateLifetime)
            return Fail("The sign-in took too long. Please try again.");
        if (saved.UserId != (http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ""))
            return Fail("You were signed in as a different user when the connection started.");
        if (!string.IsNullOrEmpty(error))
            return Fail($"Etsy returned an error: {error_description ?? error}");
        if (string.IsNullOrEmpty(code))
            return Fail("Etsy did not return an authorization code.");

        try
        {
            var baseUrl = await settings.GetAsync(SettingKeys.BaseUrl, ct) ?? "";
            var tokens = await oauth.ExchangeCodeAsync(code, saved.Verifier, UrlHelpers.EtsyRedirectUri(baseUrl), ct);

            var me = await etsy.GetMeAsync(EtsyAuth.WithToken(tokens.AccessToken), ct);
            if (me.ShopId is not { } etsyShopId)
                return Fail("That Etsy account does not own a shop. Sign in to Etsy as the shop owner and try again.");

            var info = await etsy.GetShopAsync(etsyShopId, EtsyAuth.WithToken(tokens.AccessToken), ct);
            var (shop, reconnect) = await shops.UpsertConnectedShopAsync(info, tokens, http.User.Identity?.Name, ct);

            logger.LogInformation("Etsy shop {ShopName} ({EtsyShopId}) {Action}", shop.Name, shop.EtsyShopId,
                reconnect ? "reconnected" : "connected");
            return Results.LocalRedirect(WithQuery(back, "connected", shop.Name));
        }
        catch (EtsyApiException ex)
        {
            logger.LogWarning(ex, "Etsy connection failed");
            return Fail($"Etsy rejected the request ({(int)ex.StatusCode}): {ex.Error ?? ex.Message}");
        }
        catch (EtsyNotConfiguredException ex)
        {
            return Fail(ex.Message);
        }

        IResult Fail(string message) => Results.LocalRedirect(WithQuery(back, "etsyError", message));
    }

    private static string WithQuery(string url, string key, string value) =>
        QueryHelpers.AddQueryString(url, key, value);

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
