using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;

namespace NKeeper.TestPlugins.Hello;

/// <summary>A service the plugin registers, to prove its registrations reach the application.</summary>
public sealed class Greeting(string text)
{
    public string Text { get; } = text;
}

/// <summary>
/// The well-behaved test plugin: one service, and endpoints of each kind core's policies must cover,
/// under <c>/api/p/&lt;id&gt;/</c>.
/// </summary>
public sealed class HelloPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, PluginContext context) =>
        services.AddSingleton(new Greeting($"pong from {context.Id} {context.Version}"));

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
        endpoints.MapGet("/ping", (Greeting greeting) => Results.Text(greeting.Text));
        endpoints.MapPost("/echo", (Greeting greeting) => Results.Text("echo"));

        // A raw request delegate: endpoint filters never run for these, which is why core's policies are middleware.
        endpoints.Map("/raw", context => context.Response.WriteAsync("raw"));

        // Host-scoped (SDK 0.1.1): core authorizes the host before these run.
        var use = endpoints.MapHostGroup(HostPermission.Use);
        use.MapGet("/whoami", (HostContext host) => Results.Text($"{host.Name} {host.Permission}"));
        var manage = endpoints.MapHostGroup(HostPermission.Manage);
        manage.Map("/raw", context => context.Response.WriteAsync($"managing {context.GetHostContext().Name}"));
    }
}

/// <summary>Not a plugin: the host must refuse it as an entry type.</summary>
public sealed class NotAPlugin
{
}

/// <summary>Throws while being created.</summary>
public sealed class ThrowingConstructorPlugin : IPlugin
{
    public ThrowingConstructorPlugin() => throw new InvalidOperationException("constructor failure");

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}

/// <summary>Registers a service, then throws: the host must remove what it registered.</summary>
public sealed class ThrowingConfigurePlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton(new Greeting("must be rolled back"));
        throw new InvalidOperationException("ConfigureServices failure");
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}
