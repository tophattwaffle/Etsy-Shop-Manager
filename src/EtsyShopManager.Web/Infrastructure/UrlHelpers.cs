namespace EtsyShopManager.Web.Infrastructure;

public static class UrlHelpers
{
    public const string EtsyCallbackPath = "/auth/etsy/callback";

    /// <summary>The redirect URI to register in the Etsy developer portal.</summary>
    public static string EtsyRedirectUri(string baseUrl) => baseUrl.TrimEnd('/') + EtsyCallbackPath;

    /// <summary>
    /// Validates and normalises the base URL HAProxy serves (absolute https, no query/fragment, no trailing slash).
    /// Etsy only accepts https redirect URIs.
    /// </summary>
    public static bool TryNormalizeBaseUrl(string? input, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter the URL you use to reach this app, e.g. https://etsy.example.lan";
            return false;
        }

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "Enter a full URL starting with https://";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Etsy requires an https redirect URI. Use the HTTPS hostname HAProxy serves.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "The base URL cannot contain a query string or fragment.";
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    /// <summary>True for "/path" style local URLs (blocks open redirects such as "//evil" or "/\evil").</summary>
    public static bool IsLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

    public static string SafeLocal(string? url, string fallback = "/") => IsLocalUrl(url) ? url! : fallback;
}
