using System.Text.RegularExpressions;
using EtsyShopManager.Web.Components.Layout;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Storage;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EtsyShopManager.Tests;

public class HomeAssistantIngressTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string WebProject = Path.Combine(RepoRoot, "src", "EtsyShopManager.Web");
    private static readonly string Components = Path.Combine(WebProject, "Components");

    [Theory]
    [InlineData("/api/hassio_ingress/AbC_12-3", "/api/hassio_ingress/AbC_12-3")]
    [InlineData("/api/hassio_ingress/abc/", "/api/hassio_ingress/abc")]
    [InlineData("/api/ingress/printfarm", "/api/ingress/printfarm")]
    public async Task Ingress_header_becomes_the_path_base(string header, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/orders";
        context.Request.Headers[IngressPathBaseMiddleware.HeaderName] = header;

        PathString seenBase = default;
        PathString seenPath = default;
        var middleware = new IngressPathBaseMiddleware(ctx =>
        {
            seenBase = ctx.Request.PathBase;
            seenPath = ctx.Request.Path;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(expected, seenBase.Value);
        Assert.Equal("/orders", seenPath.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("/a/../b")]
    [InlineData("/x\"><script>")]
    [InlineData("relative/path")]
    public async Task Missing_or_unsafe_ingress_header_is_ignored(string? header)
    {
        var context = new DefaultHttpContext();
        if (header is not null)
            context.Request.Headers[IngressPathBaseMiddleware.HeaderName] = header;

        var middleware = new IngressPathBaseMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);

        Assert.False(context.Request.PathBase.HasValue);
    }

    [Theory]
    [InlineData("http://localhost:5080/", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://nabucasa.example/api/ingress/test/", true)]
    [InlineData("http://localhost:8123/api/hassio_ingress/TOKEN/", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Ingress_is_detected_from_the_base_uri(string? baseUri, bool expected) =>
        Assert.Equal(expected, EmbedRoutes.IsUnderIngress(baseUri));

    [Fact]
    public void Base_href_in_App_follows_ingress_path_base()
    {
        var appFile = Path.Combine(Components, "App.razor");
        var content = File.ReadAllText(appFile);
        Assert.Contains("<base href=\"@BaseHref\" />", content);
        Assert.Contains("HttpContext.Request.PathBase + \"/\"", content);
    }

    [Fact]
    public void Drawer_initial_state_collapses_under_ingress()
    {
        var layoutFile = Path.Combine(Components, "Layout", "MainLayout.razor");
        var content = File.ReadAllText(layoutFile);
        Assert.Contains("_drawerOpen = !EmbedRoutes.IsUnderIngress(Nav.BaseUri);", content);
    }

    [Fact]
    public void Media_and_asset_helpers_return_relative_urls()
    {
        var dummyHash = new string('a', 64);
        var imageUrl = ImageStore.UrlFor($"{dummyHash}.jpg");
        Assert.NotNull(imageUrl);
        Assert.StartsWith("media/images/", imageUrl);
        Assert.False(imageUrl.StartsWith('/'));

        var videoUrl = VideoStore.UrlFor($"{dummyHash}.mp4");
        Assert.NotNull(videoUrl);
        Assert.StartsWith("media/videos/", videoUrl);
        Assert.False(videoUrl.StartsWith('/'));

        var iconUrl = ShopIconCache.UrlFor(42);
        Assert.StartsWith("media/shop-icons/42", iconUrl);
        Assert.False(iconUrl.StartsWith('/'));

        var colorCssUrl = OrderColorEngine.VersionedCssUrl;
        Assert.StartsWith("css/order-colors.css", colorCssUrl);
        Assert.False(colorCssUrl.StartsWith('/'));
    }

    [Fact]
    public void Razor_components_use_links_relative_to_base_href()
    {
        var rootAbsolute = new Regex(@"(href|src|Href|Src)\s*=\s*[""']/(?![\*\s])");
        var offenders = Directory.EnumerateFiles(Components, "*.razor", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadLines(file)
                .Select((line, i) => (file, line, i))
                .Where(x => !x.line.TrimStart().StartsWith("@page") && rootAbsolute.IsMatch(x.line))
                .Select(x => $"{Path.GetRelativePath(Components, x.file)}:{x.i + 1}: {x.line.Trim()}"))
            .ToList();

        Assert.True(offenders.Count == 0, "Root-absolute URLs detected (drop the leading /):\n" + string.Join("\n", offenders));
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EtsyShopManager.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (EtsyShopManager.slnx) not found.");
    }
}
