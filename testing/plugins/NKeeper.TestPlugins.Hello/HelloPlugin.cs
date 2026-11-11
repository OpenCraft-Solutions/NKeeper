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
    private const string StashKey = "NKeeper.TestPlugins.Hello.StashedHandle";

    /// <summary>
    /// A service type named after the plugin, so packages of this plugin under different IDs don't collide.
    /// At settings version 2: version 1 called the greeting "text".
    /// </summary>
    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        // Opens on its own service type, so packages of this plugin under different IDs don't collide here either.
        contributions.AddSessionType(new SessionType(context.Id, "Hello session", typeof(HelloSessionHandler)) { ServiceTypes = [context.Id] });
        contributions.AddHostAction(new HostAction($"{context.Id}.open", "Open", SessionType: context.Id) { Credential = HostActionCredential.None });
        contributions.AddServiceType(new ServiceType(context.Id, "Hello")
        {
            DefaultPort = 7,
            Health = HealthProbe.Tcp,
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
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton(new Greeting($"pong from {context.Id} {context.Version}"));
        services.AddScoped<HelloSessionHandler>();
    }

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

        // Records a plugin audit event (SDK 0.1.7) with the action it's given: its own, or one it mustn't use.
        use.MapPost("/audit", async (HostContext host, AuditProbe? probe, IPluginAudit audit) =>
        {
            // The authorization sweep calls every endpoint without a body.
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                await audit.RecordAsync(probe.Action, host.Id, "probed");
                return Results.Json(new { message = $"Recorded {probe.Action}." });
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: ex.Message);
            }
        });

        // Uses the broker the way a plugin would (SDK 0.1.3), and the ways one mustn't.
        use.MapPost("/credential", async (HostContext host, CredentialProbe? probe, ICredentialBroker broker) =>
        {
            // The authorization sweep calls every endpoint without a body.
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                // A host context the plugin made up, rather than the one core gave this request.
                var target = probe.Forged
                    ? new HostContext(host.Id, host.Name, host.Hostname, host.IpAddress, host.Tags, host.Permission) { Services = host.Services }
                    : host;
                var handle = probe.Stashed
                    ? AppDomain.CurrentDomain.GetData(StashKey) as CredentialHandle ?? throw new InvalidOperationException("Nothing stashed.")
                    : probe.PromptReference is { } reference
                        ? await broker.ResolvePromptAsync(target, probe.ServiceId, reference)
                        : await broker.ResolveSavedAsync(target, probe.ServiceId, probe.CredentialId ?? Guid.Empty);

                // Kept across requests, and across plugins' load contexts, for a later request to try.
                AppDomain.CurrentDomain.SetData(StashKey, handle);
                if (probe.Serialize)
                {
                    try
                    {
                        return Results.Text(System.Text.Json.JsonSerializer.Serialize(handle));
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        return Results.Text($"unserializable {handle}");
                    }
                }

                var description = await broker.DescribeAsync(target, probe.DescribeServiceId ?? probe.ServiceId, handle);
                return Results.Text($"{description.Source} {description.Username} {description.Domain} {description.HasPassword} {description.HasPrivateKey}");
            }
            catch (CredentialDeniedException ex)
            {
                return Results.Text($"denied {ex.Reason}", statusCode: StatusCodes.Status403Forbidden);
            }
        });
        // Opens an SSH connection through core (SDK 0.1.4): runs a command, or a shell.
        use.MapPost("/ssh", async (HostContext host, SshProbe? probe, ICredentialBroker broker, IConnectivity connectivity) =>
        {
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                var handle = probe.PromptReference is { } reference
                    ? await broker.ResolvePromptAsync(host, probe.ServiceId, reference)
                    : await broker.ResolveSavedAsync(host, probe.ServiceId, probe.CredentialId ?? Guid.Empty);
                await using var ssh = await connectivity.OpenSshAsync(host, probe.ServiceId, handle);
                if (!probe.Shell)
                {
                    var result = await ssh.RunAsync(probe.Command ?? "true");
                    return Results.Json(new { exitStatus = result.ExitStatus, output = result.Output, error = result.Error });
                }

                await using var shell = await ssh.OpenShellAsync(new SshTerminal(Columns: 100, Rows: 30));
                shell.Resize(120, 40);
                await shell.Stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes((probe.Command ?? "true") + "\n"));
                await shell.Stream.FlushAsync();
                var output = new System.Text.StringBuilder();
                var buffer = new byte[4096];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!output.ToString().Contains(probe.Expect ?? string.Empty, StringComparison.Ordinal))
                {
                    var read = await shell.Stream.ReadAsync(buffer, timeout.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    output.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
                }

                return Results.Json(new { output = output.ToString() });
            }
            catch (SshConnectException ex)
            {
                return Results.Json(new { reason = ex.Reason.ToString(), message = ex.Message, presentedKey = ex.PresentedKey }, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (CredentialDeniedException ex)
            {
                return Results.Text($"denied {ex.Reason}", statusCode: StatusCodes.Status403Forbidden);
            }
        });

        var manage = endpoints.MapHostGroup(HostPermission.Manage);
        manage.Map("/raw", context => context.Response.WriteAsync($"managing {context.GetHostContext().Name}"));
    }
}

/// <summary>What the credential endpoint is asked to do.</summary>
/// <param name="Stashed">Use the handle an earlier request stashed, instead of resolving one.</param>
/// <param name="Forged">Pass the broker a host context the plugin made itself.</param>
/// <param name="Serialize">Try to write the handle as JSON.</param>
public sealed record CredentialProbe(
    Guid ServiceId,
    Guid? CredentialId = null,
    string? PromptReference = null,
    Guid? DescribeServiceId = null,
    bool Stashed = false,
    bool Forged = false,
    bool Serialize = false);

/// <summary>What the SSH endpoint is asked to do.</summary>
/// <param name="Shell">Run <paramref name="Command"/> in a shell, and read until <paramref name="Expect"/> appears.</param>
public sealed record SshProbe(
    Guid ServiceId,
    Guid? CredentialId = null,
    string? PromptReference = null,
    string? Command = null,
    bool Shell = false,
    string? Expect = null);

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
    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        // Host action rules (SDK 0.1.6, run actions 0.1.7): a valid service and session type, and a host action that breaks one.
        if (context.Configuration["Break"] is { } actionRule && actionRule.StartsWith("action-", StringComparison.Ordinal))
        {
            contributions.AddServiceType(new ServiceType("fine", "Fine"));
            contributions.AddSessionType(new SessionType("fine", "Fine", typeof(HelloSessionHandler)) { ServiceTypes = ["fine"] });
            contributions.AddHostAction(actionRule switch
            {
                "action-label" => new HostAction("fine.go", " ", "fine"),
                "action-session" => new HostAction("fine.go", "Go", "elsewhere"),
                "action-credential" => new HostAction("fine.go", "Go", "fine") { Credential = (HostActionCredential)7 },
                "action-taken" => new HostAction("hello.open", "Open again", "fine"),
                "action-both" => new HostAction("fine.go", "Go", "fine") { Endpoint = "go", ServiceTypes = ["fine"] },
                "action-neither" => new HostAction("fine.go", "Go"),
                "action-endpoint" => new HostAction("fine.go", "Go") { Endpoint = "../go", ServiceTypes = ["fine"] },
                "action-run-services" => new HostAction("fine.go", "Go") { Endpoint = "go" },
                "action-session-services" => new HostAction("fine.go", "Go", "fine") { ServiceTypes = ["fine"] },
                _ => new HostAction("Fine.Go", "Go", "fine"),
            });
            return;
        }

        // Session type rules (SDK 0.1.5): a valid service type, and a session type that breaks one.
        if (context.Configuration["Break"] is { } rule && rule.StartsWith("session-", StringComparison.Ordinal))
        {
            contributions.AddServiceType(new ServiceType("fine", "Fine"));
            contributions.AddSessionType(rule switch
            {
                "session-label" => new SessionType("fine", " ", typeof(HelloSessionHandler)) { ServiceTypes = ["fine"] },
                "session-handler" => new SessionType("fine", "Fine", typeof(string)) { ServiceTypes = ["fine"] },
                "session-interface" => new SessionType("fine", "Fine", typeof(IStreamSessionHandler)) { ServiceTypes = ["fine"] },
                "session-none" => new SessionType("fine", "Fine", typeof(HelloSessionHandler)),
                "session-service" => new SessionType("fine", "Fine", typeof(HelloSessionHandler)) { ServiceTypes = ["Not Valid"] },
                "session-taken" => new SessionType("hello", "Hello again", typeof(HelloSessionHandler)) { ServiceTypes = ["fine"] },
                _ => new SessionType("Not An Id", "Bad", typeof(HelloSessionHandler)) { ServiceTypes = ["fine"] },
            });
            return;
        }

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
    }

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

/// <summary>
/// The test plugin's stream session (SDK 0.1.5). With a credential, a shell over SSH through core's connectivity,
/// like a terminal; without one, it echoes what the browser sends. <c>options.fail</c> makes opening throw.
/// </summary>
public sealed class HelloSessionHandler(IConnectivity connectivity) : IStreamSessionHandler
{
    public async Task<IStreamSession> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetProperty("fail", out var fail) && fail.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            throw new InvalidOperationException("Told to fail.");
        }

        if (request.Credential is null)
        {
            return new EchoSession(request.Host.Name);
        }

        var ssh = await connectivity.OpenSshAsync(request.Host, request.ServiceId, request.Credential, cancellationToken);
        try
        {
            return new ShellSession(ssh, await ssh.OpenShellAsync(new SshTerminal(), cancellationToken));
        }
        catch
        {
            await ssh.DisposeAsync();
            throw;
        }
    }

    private sealed class EchoSession(string hostName) : IStreamSession
    {
        public async Task RunAsync(IStreamChannel channel, CancellationToken cancellationToken)
        {
            while (await channel.ReceiveAsync(cancellationToken) is { } frame)
            {
                if (frame.IsText)
                {
                    await channel.SendTextAsync($$"""{"type":"echo","host":{{System.Text.Json.JsonSerializer.Serialize(hostName)}},"text":{{System.Text.Json.JsonSerializer.Serialize(System.Text.Encoding.UTF8.GetString(frame.Data.Span))}}}""", cancellationToken);
                }
                else
                {
                    await channel.SendAsync(frame.Data, cancellationToken);
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ShellSession(ISshConnection ssh, ISshShell shell) : IStreamSession
    {
        public async Task RunAsync(IStreamChannel channel, CancellationToken cancellationToken)
        {
            using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // A shell stream's reads may not heed cancellation; closing it ends them.
            using var close = done.Token.Register(() => shell.Stream.Dispose());
            var output = Task.Run(
                async () =>
                {
                    var buffer = new byte[4096];
                    int read;
                    while ((read = await shell.Stream.ReadAsync(buffer, done.Token)) > 0)
                    {
                        await channel.SendAsync(buffer.AsMemory(0, read), done.Token);
                    }
                },
                done.Token);
            try
            {
                while (await channel.ReceiveAsync(done.Token) is { } frame)
                {
                    if (!frame.IsText)
                    {
                        await shell.Stream.WriteAsync(frame.Data, done.Token);
                        await shell.Stream.FlushAsync(done.Token);
                    }
                }
            }
            finally
            {
                await done.CancelAsync();
                try
                {
                    await output;
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await shell.DisposeAsync();
            await ssh.DisposeAsync();
        }
    }
}

/// <summary>An audit event to record, for the plugin audit tests.</summary>
public sealed record AuditProbe(string Action);
