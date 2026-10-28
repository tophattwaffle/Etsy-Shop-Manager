using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace EtsyShopManager.Web.Infrastructure;

/// <summary>
/// Adopts a Home Assistant Ingress URL prefix from the X-Ingress-Path header into
/// HttpContext.Request.PathBase, so all links, Blazor circuits, redirects, and static assets
/// resolve correctly within Home Assistant.
/// </summary>
public partial class IngressPathBaseMiddleware
{
    public const string HeaderName = "X-Ingress-Path";

    private readonly RequestDelegate _next;

    public IngressPathBaseMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        if (TryGetPathBase(context.Request.Headers[HeaderName].ToString(), out var pathBase))
        {
            context.Request.PathBase = pathBase;
        }

        return _next(context);
    }

    public static bool TryGetPathBase(string? header, out PathString pathBase)
    {
        pathBase = PathString.Empty;
        if (string.IsNullOrWhiteSpace(header))
            return false;

        var value = header.Trim().TrimEnd('/');
        if (value.Length == 0 || value.Length > 256 || !SafePath().IsMatch(value) || value.Contains("//"))
            return false;

        pathBase = new PathString(value);
        return true;
    }

    [GeneratedRegex("^/[A-Za-z0-9_/-]+$")]
    private static partial Regex SafePath();
}
