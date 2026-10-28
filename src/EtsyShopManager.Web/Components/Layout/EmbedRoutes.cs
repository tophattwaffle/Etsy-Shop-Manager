namespace EtsyShopManager.Web.Components.Layout;

public static class EmbedRoutes
{
    /// <summary>
    /// Returns true when the app is running embedded under a Home Assistant Ingress path prefix,
    /// so the navigation drawer can start folded away on narrow screens.
    /// </summary>
    public static bool IsUnderIngress(string? baseUri) =>
        Uri.TryCreate(baseUri, UriKind.Absolute, out var uri) && uri.AbsolutePath.TrimEnd('/').Length > 0;
}
