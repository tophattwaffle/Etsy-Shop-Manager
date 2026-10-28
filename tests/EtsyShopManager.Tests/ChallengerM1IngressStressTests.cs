using System.Text.RegularExpressions;
using EtsyShopManager.Web.Components;
using EtsyShopManager.Web.Components.Layout;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Storage;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Orders;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EtsyShopManager.Tests;

public class ChallengerM1IngressStressTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string WebProject = Path.Combine(RepoRoot, "src", "EtsyShopManager.Web");
    private static readonly string Components = Path.Combine(WebProject, "Components");

    // =========================================================================
    // 1. INGRESS PATH BASE MIDDLEWARE STRESS TESTS & ATTACK VECTORS
    // =========================================================================

    [Theory]
    // Null, empty, whitespace
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    // Single & repeated slashes only
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("///")]
    [InlineData("////")]
    // Internal & leading double slashes
    [InlineData("//evil.com")]
    [InlineData("/api//ingress")]
    [InlineData("/api/ingress//sub")]
    [InlineData("/api///ingress")]
    // Path traversal attacks
    [InlineData("/..")]
    [InlineData("/../")]
    [InlineData("/../evil")]
    [InlineData("/api/../ingress")]
    [InlineData("/api/../../etc/passwd")]
    [InlineData("/api/ingress/..")]
    [InlineData("/api/ingress/.")]
    // URI schemes / Open redirect vectors
    [InlineData("http://")]
    [InlineData("http://evil.com")]
    [InlineData("https://")]
    [InlineData("https://evil.com/ingress")]
    [InlineData("ftp://files.example/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    // XSS / HTML injection vectors
    [InlineData("<script>")]
    [InlineData("/<script>")]
    [InlineData("/<script>alert(1)</script>")]
    [InlineData("/api/ingress/\"><script>")]
    [InlineData("/api/ingress/<svg onload=alert(1)>")]
    [InlineData("/api/ingress/alert(1);")]
    // Null bytes and control characters
    [InlineData("/api/\0/test")]
    [InlineData("/api/\x01/test")]
    [InlineData("/api/\x1b/test")]
    [InlineData("/api/\n/test")]
    [InlineData("/api/\r/test")]
    // Special disallowed characters
    [InlineData("/api/ingress?query=1")]
    [InlineData("/api/ingress#hash")]
    [InlineData("/api/ingress:8123")]
    [InlineData("/api/ingress@host")]
    [InlineData("/api/ingress!")]
    [InlineData("/api/ingress$")]
    [InlineData("/api/ingress%20")]
    [InlineData("/api/ingress*")]
    [InlineData("/api/ingress(1)")]
    [InlineData("/api/ingress+plus")]
    [InlineData("/api/ingress[0]")]
    [InlineData("/api/ingress{1}")]
    [InlineData("/api/ingress|pipe")]
    [InlineData("/api/ingress;semi")]
    [InlineData("/api/ingress'quote")]
    [InlineData("/api/ingress\"quote")]
    [InlineData("/api/ingress.dot")]
    [InlineData("/api/ingress,comma")]
    // Missing leading slash & backslashes
    [InlineData("api/hassio_ingress/token")]
    [InlineData("\\api\\hassio_ingress\\token")]
    [InlineData("/api\\hassio_ingress/token")]
    public void IngressPathBase_Rejects_Invalid_Or_Malicious_Headers(string? maliciousHeader)
    {
        var result = IngressPathBaseMiddleware.TryGetPathBase(maliciousHeader, out var pathBase);
        Assert.False(result, $"Expected header '{maliciousHeader}' to be rejected, but was accepted as '{pathBase.Value}'.");
        Assert.False(pathBase.HasValue);
    }

    [Fact]
    public void IngressPathBase_Rejects_Length_Exceeding_256_Characters()
    {
        // 256 chars: '/' followed by 255 'a's -> Valid
        var valid256 = "/" + new string('a', 255);
        Assert.Equal(256, valid256.Length);
        Assert.True(IngressPathBaseMiddleware.TryGetPathBase(valid256, out var pathBase256));
        Assert.Equal(valid256, pathBase256.Value);

        // 257 chars: '/' followed by 256 'a's -> Invalid (> 256)
        var invalid257 = "/" + new string('a', 256);
        Assert.Equal(257, invalid257.Length);
        Assert.False(IngressPathBaseMiddleware.TryGetPathBase(invalid257, out _));

        // 500 chars -> Invalid
        var invalid500 = "/" + new string('a', 499);
        Assert.False(IngressPathBaseMiddleware.TryGetPathBase(invalid500, out _));
    }

    [Theory]
    [InlineData("/api/hassio_ingress/abc", "/api/hassio_ingress/abc")]
    [InlineData("/api/hassio_ingress/abc/", "/api/hassio_ingress/abc")]
    [InlineData("/api/hassio_ingress/abc///", "/api/hassio_ingress/abc")]
    [InlineData("/api/hassio_ingress/AbC-123_xyz", "/api/hassio_ingress/AbC-123_xyz")]
    [InlineData("/api/ingress/printfarm", "/api/ingress/printfarm")]
    [InlineData("/sub/path/1/2/3", "/sub/path/1/2/3")]
    [InlineData("/sub/path/1/2/3/", "/sub/path/1/2/3")]
    [InlineData("/a", "/a")]
    [InlineData("/a/", "/a")]
    public void IngressPathBase_Trims_Trailing_Slashes_And_Accepts_Valid_Headers(string header, string expected)
    {
        var result = IngressPathBaseMiddleware.TryGetPathBase(header, out var pathBase);
        Assert.True(result, $"Expected header '{header}' to be accepted.");
        Assert.Equal(expected, pathBase.Value);
    }

    [Fact]
    public async Task IngressPathBaseMiddleware_Invokes_Next_And_Sets_PathBase()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/orders";
        context.Request.Headers[IngressPathBaseMiddleware.HeaderName] = "/api/hassio_ingress/TOKEN123/";

        var nextCalled = false;
        var middleware = new IngressPathBaseMiddleware(ctx =>
        {
            nextCalled = true;
            Assert.Equal("/api/hassio_ingress/TOKEN123", ctx.Request.PathBase.Value);
            Assert.Equal("/orders", ctx.Request.Path.Value);
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal("/api/hassio_ingress/TOKEN123", context.Request.PathBase.Value);
        Assert.Equal("/orders", context.Request.Path.Value);
    }

    [Fact]
    public async Task IngressPathBaseMiddleware_Leaves_PathBase_Untouched_When_Header_Invalid()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/orders";
        context.Request.PathBase = "/existing/base";
        context.Request.Headers[IngressPathBaseMiddleware.HeaderName] = "<script>alert(1)</script>";

        var nextCalled = false;
        var middleware = new IngressPathBaseMiddleware(ctx =>
        {
            nextCalled = true;
            Assert.Equal("/existing/base", ctx.Request.PathBase.Value);
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal("/existing/base", context.Request.PathBase.Value);
    }

    // =========================================================================
    // 2. DYNAMIC <base href> IN App.razor
    // =========================================================================

    [Theory]
    [InlineData("", "/")]
    [InlineData("/api/ingress/token", "/api/ingress/token/")]
    [InlineData("/api/hassio_ingress/abc_123", "/api/hassio_ingress/abc_123/")]
    [InlineData("/standalone", "/standalone/")]
    public void BaseHref_Expression_Produces_Correct_Trailing_Slash_Uri(string initialPathBase, string expectedBaseHref)
    {
        var pathBase = string.IsNullOrEmpty(initialPathBase) ? PathString.Empty : new PathString(initialPathBase);
        var baseHref = pathBase + "/";
        Assert.Equal(expectedBaseHref, baseHref);
    }

    [Fact]
    public void AppRazor_Declares_Dynamic_BaseHref()
    {
        var appPath = Path.Combine(Components, "App.razor");
        Assert.True(File.Exists(appPath));
        var content = File.ReadAllText(appPath);

        // Verify base href tag
        Assert.Contains("<base href=\"@BaseHref\" />", content);

        // Verify BaseHref property implementation
        Assert.Contains("private string BaseHref => HttpContext.Request.PathBase + \"/\";", content);
    }

    // =========================================================================
    // 3. EMPIRICAL SCAN: NO ROOT-ABSOLUTE '/' PATHS IN RAZOR OR CS
    // =========================================================================

    [Fact]
    public void All_Razor_Files_Do_Not_Use_Root_Absolute_Urls()
    {
        // Matches href="/...", src="/...", Href="/...", Src="/...", action="/...", Action="/..."
        // but ignores @page "/..." and razor comments / XML docs
        var rootAbsoluteAttrRegex = new Regex(@"(href|src|Href|Src|action|Action)\s*=\s*[""']/(?![\*\s/])");

        var offenders = new List<string>();

        var razorFiles = Directory.EnumerateFiles(WebProject, "*.razor", SearchOption.AllDirectories);
        foreach (var file in razorFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("@page", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (rootAbsoluteAttrRegex.IsMatch(line))
                {
                    offenders.Add($"{Path.GetRelativePath(WebProject, file)}:{i + 1}: {line}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"Found {offenders.Count} root-absolute URLs in .razor files escaping <base href>:\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void All_Cs_Files_In_Web_Do_Not_Navigate_To_Root_Absolute_Urls()
    {
        // NavigateTo("/...") without base href escapes the ingress prefix
        var navigateToRootAbsolute = new Regex(@"NavigateTo\s*\(\s*[""']/(?!/)");
        var offenders = new List<string>();

        var csFiles = Directory.EnumerateFiles(WebProject, "*.cs", SearchOption.AllDirectories);
        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (navigateToRootAbsolute.IsMatch(line))
                {
                    offenders.Add($"{Path.GetRelativePath(WebProject, file)}:{i + 1}: {line}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"Found NavigateTo calls with root-absolute '/' in .cs files:\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void Url_Helpers_Return_Relative_Urls_Without_Leading_Slash()
    {
        var dummyHash = new string('f', 64);

        // ImageStore
        var imgUrl = ImageStore.UrlFor($"{dummyHash}.jpg");
        Assert.NotNull(imgUrl);
        Assert.False(imgUrl.StartsWith('/'), $"ImageStore.UrlFor returned leading slash: {imgUrl}");
        Assert.StartsWith("media/images/", imgUrl);

        // VideoStore
        var videoUrl = VideoStore.UrlFor($"{dummyHash}.mp4");
        Assert.NotNull(videoUrl);
        Assert.False(videoUrl.StartsWith('/'), $"VideoStore.UrlFor returned leading slash: {videoUrl}");
        Assert.StartsWith("media/videos/", videoUrl);

        // ShopIconCache
        var iconUrl = ShopIconCache.UrlFor(12345);
        Assert.NotNull(iconUrl);
        Assert.False(iconUrl.StartsWith('/'), $"ShopIconCache.UrlFor returned leading slash: {iconUrl}");
        Assert.StartsWith("media/shop-icons/12345", iconUrl);

        // OrderColorEngine.VersionedCssUrl
        var cssUrl = OrderColorEngine.VersionedCssUrl;
        Assert.NotNull(cssUrl);
        Assert.False(cssUrl.StartsWith('/'), $"OrderColorEngine.VersionedCssUrl returned leading slash: {cssUrl}");
        Assert.StartsWith("css/order-colors.css", cssUrl);

        // UrlHelpers.SafeLocal & SafeRelative
        Assert.Equal("orders", UrlHelpers.SafeLocal("/orders"));
        Assert.Equal("orders", UrlHelpers.SafeRelative("/orders"));
        Assert.Equal("", UrlHelpers.SafeLocal("/"));
        Assert.Equal("", UrlHelpers.SafeRelative("/"));
        Assert.Equal("", UrlHelpers.SafeLocal("//evil.com"));
        Assert.Equal("", UrlHelpers.SafeRelative("//evil.com"));
    }

    // =========================================================================
    // 4. VERIFY EmbedRoutes.IsUnderIngress UNDER VARIOUS URI CONFIGURATIONS
    // =========================================================================

    [Theory]
    // Standalone / root URIs (must be FALSE)
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("invalid-uri", false)]
    [InlineData("relative/path", false)]
    [InlineData("http://localhost:5080/", false)]
    [InlineData("http://localhost:5080", false)]
    [InlineData("https://etsy.example.com/", false)]
    [InlineData("https://etsy.example.com", false)]
    [InlineData("http://localhost:5080/?param=1", false)]
    [InlineData("http://localhost:5080/#frag", false)]
    [InlineData("http://[::1]:5080/", false)]
    // Embedded / Ingress URIs with path prefix (must be TRUE)
    [InlineData("http://localhost:8123/api/hassio_ingress/token", true)]
    [InlineData("http://localhost:8123/api/hassio_ingress/token/", true)]
    [InlineData("https://ha.domain.com:8123/api/hassio_ingress/token_abc-123/", true)]
    [InlineData("http://127.0.0.1:8123/ingress/subpath", true)]
    [InlineData("http://example.com/ingress", true)]
    [InlineData("http://example.com/ingress/", true)]
    [InlineData("http://example.com/sub/deep/path", true)]
    [InlineData("https://nabucasa.example/api/ingress/test/", true)]
    public void EmbedRoutes_IsUnderIngress_Evaluates_Correctly(string? baseUri, bool expected)
    {
        var actual = EmbedRoutes.IsUnderIngress(baseUri);
        Assert.Equal(expected, actual);
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
