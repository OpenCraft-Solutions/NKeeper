using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NKeeper.Abstractions;

/// <summary>
/// What a plugin knows about itself at run time. Its identity comes from its verified package
/// manifest, never from the plugin's own code.
/// </summary>
/// <param name="Id">The plugin ID, e.g. <c>ssh</c>.</param>
/// <param name="Version">The plugin version, as in its manifest.</param>
/// <param name="Configuration">The plugin's own configuration section, <c>Plugins:Settings:&lt;id&gt;</c>.</param>
public sealed record PluginContext(string Id, string Version, IConfiguration Configuration);

/// <summary>
/// A plugin's entry point (SDK v0, docs/v2-plan.md §4.1). The manifest names the implementing type;
/// the server creates one instance with a public parameterless constructor, at startup.
/// </summary>
/// <remarks>
/// More members arrive with their first real users (§4.2). Until SDK v1 is frozen at the end of
/// Phase 3, this contract may change.
/// </remarks>
public interface IPlugin
{
    /// <summary>Registers the plugin's services. Called once, before the application is built.</summary>
    void ConfigureServices(IServiceCollection services, PluginContext context);

    /// <summary>
    /// Maps the plugin's endpoints. <paramref name="endpoints"/> is the route group core created for
    /// this plugin at <c>/api/p/&lt;id&gt;/</c>; core applies its security policies to it.
    /// </summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context);
}
