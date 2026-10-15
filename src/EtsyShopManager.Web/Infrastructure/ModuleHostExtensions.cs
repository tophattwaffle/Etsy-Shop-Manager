using EtsyShopManager.Core.Modules;

namespace EtsyShopManager.Web.Infrastructure;

public static class ModuleHostExtensions
{
    /// <summary>Lets each module register its services, and makes the module list available to the UI.</summary>
    public static ModuleRegistry AddModules(this WebApplicationBuilder builder, params IModule[] modules)
    {
        foreach (var module in modules)
            module.ConfigureServices(builder.Services, builder.Configuration);

        var registry = new ModuleRegistry(modules);
        builder.Services.AddSingleton(registry);
        return registry;
    }

    public static void MapModuleEndpoints(this WebApplication app, ModuleRegistry registry)
    {
        foreach (var module in registry.Modules)
            module.MapEndpoints(app);
    }
}
