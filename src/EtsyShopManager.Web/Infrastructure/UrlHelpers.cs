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

    /// <summary>True for in-app relative paths ("orders", "admin/settings", "") or "/path" style local URLs.</summary>
    public static bool IsSafeRelative(string? url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        if (url.StartsWith("//") || url.StartsWith("/\\") || url.StartsWith('\\')) return false;
        var colon = url.IndexOf(':');
        var slash = url.IndexOf('/');
        if (colon >= 0 && (slash < 0 || colon < slash)) return false;
        return true;
    }

    /// <summary>
    /// Returns a safe relative path for in-app client navigation (Blazor NavigationManager.NavigateTo),
    /// defaulting to "" so it resolves against the HTML base href rather than escaping to the host root "/".
    /// </summary>
    public static string SafeRelative(string? url, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(url)) return fallback;
        if (!IsSafeRelative(url)) return fallback;
        var trimmed = url.Trim();
        return trimmed.StartsWith('/') ? trimmed.TrimStart('/') : trimmed;
    }

    /// <summary>
    /// Safe local URL for client navigation or redirects. Defaults to "" (dashboard under base href)
    /// to avoid escaping base href when running under Ingress.
    /// </summary>
    public static string SafeLocal(string? url, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(url)) return fallback;
        if (IsLocalUrl(url))
            return string.IsNullOrEmpty(fallback) ? url.TrimStart('/') : url;
        if (IsSafeRelative(url)) return url.Trim();
        return fallback;
    }
}
