using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;

namespace NKeeper.Smoke;

/// <summary>The smallest plugin there is: one endpoint, which says who served it.</summary>
public sealed class SmokePlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context) =>
        endpoints.MapGet("/ping", () => Results.Text($"pong from {context.Id} {context.Version}"));
}
