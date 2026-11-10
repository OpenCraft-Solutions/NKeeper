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
    /// <summary>
    /// A service type named after the plugin, so packages of this plugin under different IDs don't collide.
    /// At settings version 2: version 1 called the greeting "text".
    /// </summary>
    public void Contribute(IPluginContributions contributions, PluginContext context) =>
        contributions.AddServiceType(new ServiceType(context.Id, "Hello")
        {
            DefaultPort = 7,
            SettingsVersion = 2,
            Settings =
            [
                ServiceSetting.Text("greeting", "Greeting", maxLength: 20, required: true),
                ServiceSetting.Number("repeat", "Repeat", minimum: 1, maximum: 5),
                ServiceSetting.Switch("loud", "Loud"),
                ServiceSetting.Choice("language", "Language", ["en", "fr"]),
            ],
            Upgrade = (from, settings) =>
            {
                if (from < 2 && settings.TryGetPropertyValue("text", out var text))
                {
                    settings.Remove("text");
                    settings["greeting"] = text;
                }

                return settings;
            },
        });

    public void ConfigureServices(IServiceCollection services, PluginContext context) =>
        services.AddSingleton(new Greeting($"pong from {context.Id} {context.Version}"));

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
        endpoints.MapGet("/ping", (Greeting greeting) => Results.Text(greeting.Text));
        endpoints.MapPost("/echo", (Greeting greeting) => Results.Text("echo"));

        // A raw request delegate: endpoint filters never run for these, which is why core's policies are middleware.
        endpoints.Map("/raw", context => context.Response.WriteAsync("raw"));

        // Host-scoped (SDK 0.1.1; services 0.1.2): core authorizes the host before these run.
        var use = endpoints.MapHostGroup(HostPermission.Use);
        use.MapGet("/whoami", (HostContext host) => Results.Text($"{host.Name} {host.Permission}"));
        use.MapGet("/services", (HostContext host) => Results.Json(host.Services));
        var manage = endpoints.MapHostGroup(HostPermission.Manage);
        manage.Map("/raw", context => context.Response.WriteAsync($"managing {context.GetHostContext().Name}"));
    }
}

/// <summary>Contributes the service type <c>hello</c> whatever its own ID: the host must refuse it when <c>hello</c> is taken.</summary>
public sealed class DuplicateServiceTypePlugin : IPlugin
{
    public void Contribute(IPluginContributions contributions, PluginContext context) =>
        contributions.AddServiceType(new ServiceType("hello", "Hello again"));

    public void ConfigureServices(IServiceCollection services, PluginContext context) =>
        services.AddSingleton(new Greeting("must not be registered"));

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}

/// <summary>Contributes a service type that breaks a rule, chosen by its configuration's <c>Break</c>.</summary>
public sealed class InvalidServiceTypePlugin : IPlugin
{
    public void Contribute(IPluginContributions contributions, PluginContext context) =>
        contributions.AddServiceType(context.Configuration["Break"] switch
        {
            "label" => new ServiceType("fine", " "),
            "port" => new ServiceType("fine", "Fine") { DefaultPort = 70000 },
            "version" => new ServiceType("fine", "Fine") { SettingsVersion = 2 },
            "key" => new ServiceType("fine", "Fine") { Settings = [ServiceSetting.Switch("Not-Camel", "Bad")] },
            "twice" => new ServiceType("fine", "Fine") { Settings = [ServiceSetting.Switch("a", "A"), ServiceSetting.Switch("a", "A")] },
            "length" => new ServiceType("fine", "Fine") { Settings = [ServiceSetting.Text("a", "A", maxLength: 5000)] },
            "range" => new ServiceType("fine", "Fine") { Settings = [ServiceSetting.Number("a", "A", minimum: 5, maximum: 1)] },
            "choices" => new ServiceType("fine", "Fine") { Settings = [ServiceSetting.Choice("a", "A", [])] },
            "null-settings" => new ServiceType("fine", "Fine") { Settings = null! },
            "id-newline" => new ServiceType("fine\n", "Fine"),
            "key-newline" => new ServiceType("fine", "Fine") { Settings = [ServiceSetting.Switch("a\n", "A")] },
            _ => new ServiceType("Not An Id", "Bad"),
        });

    public void ConfigureServices(IServiceCollection services, PluginContext context) =>
        services.AddSingleton(new Greeting("must not be registered"));

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}

/// <summary>Throws while contributing.</summary>
public sealed class ThrowingContributePlugin : IPlugin
{
    public void Contribute(IPluginContributions contributions, PluginContext context) =>
        throw new InvalidOperationException("Contribute failure");

    public void ConfigureServices(IServiceCollection services, PluginContext context) =>
        services.AddSingleton(new Greeting("must not be registered"));

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
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
