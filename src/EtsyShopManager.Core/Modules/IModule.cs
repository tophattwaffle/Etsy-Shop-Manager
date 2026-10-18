using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Core.Modules;

/// <summary>
/// A feature module. It registers its own services, job handlers, recurring jobs, API endpoints,
/// navigation entries and (optionally) an assembly with Razor pages.
/// </summary>
public interface IModule
{
    string Name { get; }

    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints) { }

    IEnumerable<NavItem> NavItems => [];

    /// <summary>Assembly containing this module's routable Razor components, if not the host assembly.</summary>
    Assembly? ComponentAssembly => null;
}

/// <param name="Icon">Icon identifier understood by the UI (MudBlazor SVG icon string).</param>
/// <param name="Roles">Comma-separated roles allowed to see the item; null means any signed-in user.</param>
/// <param name="Group">Collapsible sidebar group the item sits in (see <see cref="NavGroups"/>); null shows it at the top level.</param>
public sealed record NavItem(string Title, string Href, string Icon, int Order = 100, string? Roles = null, bool MatchAll = false,
    string? Group = null);

/// <summary>Sidebar groups, so rarely used pages fold away under one heading.</summary>
public static class NavGroups
{
    public const string Admin = "Admin";
}

/// <summary>The loaded modules, available to the UI and host.</summary>
public sealed class ModuleRegistry(IReadOnlyList<IModule> modules)
{
    public IReadOnlyList<IModule> Modules { get; } = modules;

    public IEnumerable<NavItem> NavItems => Modules.SelectMany(m => m.NavItems).OrderBy(n => n.Order);

    public IEnumerable<Assembly> ComponentAssemblies =>
        Modules.Select(m => m.ComponentAssembly).OfType<Assembly>().Distinct();
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Editor = "Editor";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Admin, Editor, Viewer];
}
