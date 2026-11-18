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

        // A session origin (SDK 0.1.8), only when its settings name a prefix: the tests package this plugin under many IDs.
        if (context.Configuration["Origin"] is { } prefix)
        {
            contributions.AddSessionOrigin(new SessionOrigin(prefix, typeof(HelloOriginHandler)));
        }
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
        services.AddSingleton<HelloOriginHandler>();
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
                // Another port on the service's address (SDK 1.6), when the probe names one.
                await using var ssh = probe.Port is { } port
                    ? await connectivity.OpenSshAsync(host, probe.ServiceId, handle, port)
                    : await connectivity.OpenSshAsync(host, probe.ServiceId, handle);
                if (probe.ForwardPort is { } forwardPort)
                {
                    // A port on the SSH server's own loopback, forwarded (SDK 1.11): what answers there first.
                    await using var forwarded = await ssh.ForwardAsync("127.0.0.1", forwardPort);
                    var greeting = new byte[256];
                    using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var got = await forwarded.Stream.ReadAsync(greeting, waiting.Token);
                    return Results.Json(new { forwarded = System.Text.Encoding.ASCII.GetString(greeting, 0, got), address = forwarded.Address, port = forwarded.Port });
                }

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
            catch (ConnectException ex)
            {
                return Results.Json(new { reason = ex.Reason.ToString(), message = ex.Message, presentedKey = ex.Presented }, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (CredentialDeniedException ex)
            {
                return Results.Text($"denied {ex.Reason}", statusCode: StatusCodes.Status403Forbidden);
            }
        });

        // Opens an SFTP connection through core (SDK 1.3) and works through a folder of its own in the home folder,
        // reporting each step: what it returned, or the failure's reason.
        use.MapPost("/sftp", async (HostContext host, SshProbe? probe, ICredentialBroker broker, IConnectivity connectivity) =>
        {
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                var handle = await broker.ResolveSavedAsync(host, probe.ServiceId, probe.CredentialId ?? Guid.Empty);
                await using var sftp = await connectivity.OpenSftpAsync(host, probe.ServiceId, handle);
                var steps = new List<string>();
                async Task Step(string name, Func<Task<string>> step)
                {
                    try
                    {
                        steps.Add($"{name}: {await step()}");
                    }
                    catch (SftpException ex)
                    {
                        steps.Add($"{name}: {ex.Reason}");
                    }
                }

                var folder = sftp.HomeDirectory.TrimEnd('/') + "/" + (probe.Command ?? "probe");
                var file = folder + "/a.txt";
                await Step("home", () => Task.FromResult(sftp.HomeDirectory.StartsWith('/') ? "absolute" : sftp.HomeDirectory));
                await Step("mkdir", async () => { await sftp.CreateDirectoryAsync(folder); return "ok"; });
                await Step("mkdir again", async () => { await sftp.CreateDirectoryAsync(folder); return "ok"; });
                await Step("write", async () =>
                {
                    await using var stream = await sftp.OpenWriteAsync(file, overwrite: false);
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("hello sftp"));
                    return "ok";
                });
                await Step("write again", async () => { await using var stream = await sftp.OpenWriteAsync(file, overwrite: false); return "ok"; });
                await Step("overwrite", async () =>
                {
                    await using var stream = await sftp.OpenWriteAsync(file, overwrite: true);
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("hello again"));
                    return "ok";
                });
                await Step("read", async () =>
                {
                    await using var stream = await sftp.OpenReadAsync(file);
                    using var reader = new StreamReader(stream);
                    return await reader.ReadToEndAsync();
                });
                await Step("list", async () => string.Join(',', (await sftp.ListAsync(folder)).Select(e => $"{e.Name} {e.Kind} {e.Size} {e.Permissions.Length} {e.Path == file}")));
                await Step("get", async () => (await sftp.GetAsync(folder)) is { Kind: SftpEntryKind.Directory } entry && entry.Path == folder ? "directory" : "wrong");
                await Step("delete full folder", async () => { await sftp.DeleteDirectoryAsync(folder); return "ok"; });
                await Step("rename", async () => { await sftp.RenameAsync(file, folder + "/b.txt"); return "ok"; });
                await Step("rename over", async () =>
                {
                    await using (await sftp.OpenWriteAsync(file, overwrite: false))
                    {
                    }

                    await sftp.RenameAsync(file, folder + "/b.txt");
                    return "ok";
                });
                await Step("delete", async () => { await sftp.DeleteFileAsync(file); await sftp.DeleteFileAsync(folder + "/b.txt"); return "ok"; });
                await Step("delete folder", async () => { await sftp.DeleteDirectoryAsync(folder); return "ok"; });
                await Step("missing", async () => (await sftp.GetAsync(folder)).Name);
                await Step("forbidden", async () => { await using var stream = await sftp.OpenWriteAsync("/etc/nkeeper-probe", overwrite: false); return "ok"; });
                await Step("unreadable", async () => string.Join(',', (await sftp.ListAsync("/root")).Select(e => e.Name)));
                return Results.Json(steps);
            }
            catch (ConnectException ex)
            {
                return Results.Json(new { reason = ex.Reason.ToString(), message = ex.Message, presentedKey = ex.Presented }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Opens a database connection through core with Npgsql (SDK 1.7), and a second one, and says who they signed in as.
        // With Keep, the driver is a fake that keeps the password, which core must refuse.
        use.MapPost("/db", async (HostContext host, DatabaseProbe? probe, ICredentialBroker broker, IConnectivity connectivity) =>
        {
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                var handle = await broker.ResolveSavedAsync(host, probe.ServiceId, probe.CredentialId ?? Guid.Empty);
                var request = new DatabaseConnectRequest(
                    probe.Throw ? KeepingFactory.Throwing : probe.Keep ? KeepingFactory.Instance : Npgsql.NpgsqlFactory.Instance,
                    endpoint => new Npgsql.NpgsqlConnectionStringBuilder { Host = endpoint.Address, Port = endpoint.Port, Database = probe.Database, Pooling = false, PersistSecurityInfo = true },
                    "Username",
                    "Password")
                {
                    DriverKeepsPassword = probe.DriverKeeps,
                };
                await using var login = await connectivity.OpenDatabaseAsync(host, probe.ServiceId, handle, request);
                if (probe.Keep)
                {
                    // The keeping driver runs no commands: it was handed over, password and all (SDK 1.10).
                    return Results.Json(new { kept = login.Connection.ConnectionString.Contains("Password", StringComparison.OrdinalIgnoreCase) });
                }

                await using var command = login.Connection.CreateCommand();
                command.CommandText = "SELECT current_user";
                var first = (string?)await command.ExecuteScalarAsync();
                var another = await login.OpenAnotherAsync();
                await using var second = another.CreateCommand();
                second.CommandText = "SELECT current_user";
                return Results.Json(new
                {
                    first,
                    second = (string?)await second.ExecuteScalarAsync(),
                    passwordInConnectionString = login.Connection.ConnectionString.Contains("Password", StringComparison.OrdinalIgnoreCase),
                });
            }
            catch (ConnectException ex)
            {
                return Results.Json(new { reason = ex.Reason.ToString(), message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Opens a plain TCP connection to a service (SDK 1.9), sends a line and reads the answer, as a plugin reading a
        // server's pre-login would.
        use.MapPost("/tcp", async (HostContext host, TcpProbe? probe, IConnectivity connectivity) =>
        {
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                await using var tcp = await connectivity.OpenTcpAsync(host, probe.ServiceId);
                await tcp.Stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(probe.Send + "\n"));
                var buffer = new byte[256];
                var read = await tcp.Stream.ReadAsync(buffer);
                return Results.Json(new { address = tcp.Address, port = tcp.Port, answer = System.Text.Encoding.ASCII.GetString(buffer, 0, read) });
            }
            catch (ConnectException ex)
            {
                return Results.Json(new { reason = ex.Reason.ToString(), message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Checks a made-up certificate against the trust store, as a plugin does with one it read in its own protocol (SDK 1.7).
        use.MapPost("/identity", async (HostContext host, IdentityProbe? probe, IConnectivity connectivity) =>
        {
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                var identity = PresentedIdentity.Certificate(host.Id, probe.ServiceId, probe.Fingerprint, "CN=sql.test", DateTimeOffset.UtcNow.AddDays(30));
                await connectivity.CheckIdentityAsync(host, probe.ServiceId, identity, probe.PubliclyValid);
                return Results.Json(new { ok = true });
            }
            catch (ConnectException ex)
            {
                return Results.Json(new { reason = ex.Reason.ToString(), message = ex.Message, presented = ex.Presented }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Opens an HTTPS connection core signs in to (SDK 1.6) and GETs a path, answering the status and body.
        use.MapPost("/https", async (HostContext host, SshProbe? probe, ICredentialBroker broker, IConnectivity connectivity) =>
        {
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                var handle = await broker.ResolveSavedAsync(host, probe.ServiceId, probe.CredentialId ?? Guid.Empty);
                await using var http = await connectivity.OpenHttpAsync(host, probe.ServiceId, handle);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(http.BaseAddress, probe.Command ?? "/"));

                // The plugin's own authorization is replaced by core's.
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "cGx1Z2luOm1hZGUtdXA=");
                using var response = await http.Client.SendAsync(request, CancellationToken.None);
                return Results.Json(new { status = (int)response.StatusCode, body = await response.Content.ReadAsStringAsync() });
            }
            catch (ConnectException ex)
            {
                return Results.Json(new { reason = ex.Reason.ToString(), message = ex.Message, presented = ex.Presented }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Opens a WMI connection through core (SDK 1.5) and runs each query on it in turn, reporting its rows or why not.
        use.MapPost("/wmi", async (HostContext host, WindowsProbe? probe, ICredentialBroker broker, IConnectivity connectivity) =>
        {
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                var handle = probe.CredentialId is { } id ? await broker.ResolveSavedAsync(host, probe.ServiceId, id) : await broker.ResolvePromptAsync(host, probe.ServiceId, probe.PromptReference ?? string.Empty);
                await using var wmi = await connectivity.OpenWmiAsync(host, probe.ServiceId, handle);
                var answers = new List<object>();
                foreach (var wql in probe.Queries ?? [])
                {
                    try
                    {
                        answers.Add(await wmi.QueryAsync(new WmiQuery(wql) { Limit = probe.Limit, TimeProperty = probe.Since is null ? null : "TimeGenerated", Since = probe.Since }));
                    }
                    catch (WmiException ex)
                    {
                        answers.Add(new { refused = ex.Message });
                    }
                }

                return Results.Json(answers);
            }
            catch (ConnectException ex)
            {
                return Results.Json(new { reason = ex.Reason.ToString(), message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Binds the endpoint mapper over RPC through core (SDK 1.5) and makes one call per stub, in hex.
        use.MapPost("/rpc", async (HostContext host, WindowsProbe? probe, ICredentialBroker broker, IConnectivity connectivity) =>
        {
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                var handle = await broker.ResolveSavedAsync(host, probe.ServiceId, probe.CredentialId ?? Guid.Empty);
                await using var rpc = await connectivity.OpenRpcAsync(host, probe.ServiceId, handle, RpcInterface.EndpointMapper);
                var answers = new List<string>();
                foreach (var call in probe.Queries ?? [])
                {
                    var (opnum, stub) = (ushort.Parse(call.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture), Convert.FromHexString(call.Split(':')[1]));
                    try
                    {
                        answers.Add(Convert.ToHexString(await rpc.CallAsync(opnum, stub)));
                    }
                    catch (RpcFaultException ex)
                    {
                        answers.Add($"fault {ex.Status}");
                    }
                }

                return Results.Json(answers);
            }
            catch (ConnectException ex)
            {
                return Results.Json(new { reason = ex.Reason.ToString(), message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Streams a command's output through core (SDK 1.3): reads it all, or with Shell stops it after Expect arrives,
        // then shows the connection still works and the command ended.
        use.MapPost("/stream", async (HostContext host, SshProbe? probe, ICredentialBroker broker, IConnectivity connectivity) =>
        {
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            var handle = await broker.ResolveSavedAsync(host, probe.ServiceId, probe.CredentialId ?? Guid.Empty);
            await using var ssh = await connectivity.OpenSshAsync(host, probe.ServiceId, handle);
            var started = System.Diagnostics.Stopwatch.StartNew();
            var command = await ssh.StartAsync(probe.Command ?? "true");
            if (probe.InputBase64 is { } input)
            {
                // SDK 1.12: the command's input, then its end, as a pipe's.
                await command.Input.WriteAsync(Convert.FromBase64String(input));
                await command.Input.FlushAsync();
                await command.Input.DisposeAsync();
            }

            using var reader = new StreamReader(command.Output);
            var lines = new List<string>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add($"{line} at {started.ElapsedMilliseconds / 100}");
                if (probe.Shell && line == probe.Expect)
                {
                    break;
                }
            }

            SshCommandResult? result = null;
            if (probe.Shell)
            {
                await command.DisposeAsync();
            }
            else
            {
                result = await command.WaitAsync(timeout.Token);
                await command.DisposeAsync();
            }

            // The connection still works, and the command (a "sleep 37") is gone.
            await Task.Delay(500);
            var after = await ssh.RunAsync("pgrep -f 'sleep 3[7]' >/dev/null && echo running || echo gone");
            return Results.Json(new { lines, exitStatus = result?.ExitStatus, error = result?.Error, after = after.Output, seconds = started.Elapsed.TotalSeconds });
        });

        // Opens a Telnet terminal through core (SDK 1.1): follows the steps (wait for text, then type a line), then
        // returns what the terminal showed.
        use.MapPost("/telnet", async (HostContext host, TelnetProbe? probe, ICredentialBroker broker, IConnectivity connectivity) =>
        {
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                var handle = probe.CredentialId is { } credentialId ? await broker.ResolveSavedAsync(host, probe.ServiceId, credentialId) : null;
                await using var telnet = await connectivity.OpenTelnetAsync(host, probe.ServiceId, handle, new TerminalSize(100, 30));
                telnet.Resize(120, 40);
                var output = new System.Text.StringBuilder();
                var buffer = new byte[4096];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                foreach (var step in probe.Steps ?? [])
                {
                    var from = output.Length;
                    while (!output.ToString(from, output.Length - from).Contains(step.Expect, StringComparison.Ordinal))
                    {
                        var read = await telnet.Stream.ReadAsync(buffer, timeout.Token);
                        if (read == 0)
                        {
                            return Results.Json(new { output = output.ToString(), closed = true });
                        }

                        output.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
                    }

                    if (step.Send is { } send)
                    {
                        await telnet.Stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(send + "\r"));
                    }
                }

                return Results.Json(new { output = output.ToString(), closed = false });
            }
            catch (ConnectException ex)
            {
                return Results.Json(new { reason = ex.Reason.ToString(), message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (CredentialDeniedException ex)
            {
                return Results.Text($"denied {ex.Reason}", statusCode: StatusCodes.Status403Forbidden);
            }
        });

        // Opens a remote desktop through core (SDK 1.2 for VNC), and returns the opcodes guacd sent until it drew
        // something or reported an error.
        use.MapPost("/desktop", async (HostContext host, DesktopProbe? probe, ICredentialBroker broker, IConnectivity connectivity) =>
        {
            if (probe is null)
            {
                return Results.Text("no probe");
            }

            try
            {
                var handle = await broker.ResolveSavedAsync(host, probe.ServiceId, probe.CredentialId);
                var options = new DesktopOptions(1024, 768) { Protocol = probe.Protocol, Security = probe.Security };
                await using var desktop = await connectivity.OpenDesktopAsync(host, probe.ServiceId, handle, options);
                var opcodes = new List<string>();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!opcodes.Contains("img") && !opcodes.Contains("error") && await desktop.ReceiveAsync(timeout.Token) is { } text)
                {
                    opcodes.AddRange(System.Text.RegularExpressions.Regex.Matches(text, @"(?:^|;)\d+\.([a-z-]+)").Select(m => m.Groups[1].Value));
                    if (opcodes.Count == 1)
                    {
                        // The browser's size, through core's allow-list, as the Guacamole client sends it.
                        await desktop.SendFromBrowserAsync("4.size,4.1024,3.768,2.96;", timeout.Token);
                    }
                }

                return Results.Json(new { opcodes });
            }
            catch (ConnectException ex)
            {
                return Results.Json(new { reason = ex.Reason.ToString(), message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (ArgumentException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status400BadRequest);
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
public sealed record TelnetProbe(Guid ServiceId, Guid? CredentialId = null, IReadOnlyList<TelnetStep>? Steps = null);

/// <summary>What the desktop endpoint opens.</summary>
public sealed record DesktopProbe(Guid ServiceId, Guid CredentialId, DesktopProtocol Protocol = DesktopProtocol.Rdp, DesktopSecurity Security = DesktopSecurity.Any);

/// <summary>Waits for <paramref name="Expect"/> in the terminal, then types <paramref name="Send"/> and Enter.</summary>
public sealed record TelnetStep(string Expect, string? Send = null);

public sealed record DatabaseProbe(Guid ServiceId, Guid? CredentialId = null, string? Database = null, bool Keep = false, bool Throw = false, bool DriverKeeps = false);

public sealed record TcpProbe(Guid ServiceId, string Send = "ping");

public sealed record IdentityProbe(Guid ServiceId, string Fingerprint, bool PubliclyValid = false);

/// <summary>A driver that keeps the password in its connection string after opening, as core must refuse (SDK 1.7).</summary>
public sealed class KeepingFactory : System.Data.Common.DbProviderFactory
{
    public static readonly KeepingFactory Instance = new(throwing: false);

    /// <summary>A driver whose error isn't a DbException, and repeats the connection string, password and all (SDK 1.9).</summary>
    public static readonly KeepingFactory Throwing = new(throwing: true);

    private readonly bool _throwing;

    private KeepingFactory(bool throwing) => _throwing = throwing;

    public override System.Data.Common.DbConnection CreateConnection() => new KeepingConnection(_throwing);

    private sealed class KeepingConnection(bool throwing) : System.Data.Common.DbConnection
    {
        private System.Data.ConnectionState _state = System.Data.ConnectionState.Closed;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override System.Data.ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close() => _state = System.Data.ConnectionState.Closed;

        public override void Open() => _state = throwing ? throw new InvalidOperationException($"Refused: {ConnectionString}") : System.Data.ConnectionState.Open;

        protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override System.Data.Common.DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}

/// <summary>WMI: the queries to run. RPC: the calls to make, each <c>opnum:hex stub</c>.</summary>
public sealed record WindowsProbe(Guid ServiceId, Guid? CredentialId = null, string? PromptReference = null, IReadOnlyList<string>? Queries = null, int? Limit = null, DateTimeOffset? Since = null);

public sealed record SshProbe(
    Guid ServiceId,
    Guid? CredentialId = null,
    string? PromptReference = null,
    string? Command = null,
    bool Shell = false,
    string? Expect = null,
    int? Port = null,
    int? ForwardPort = null,
    string? InputBase64 = null);

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
        // Session origin rules (SDK 0.1.8): an origin that breaks one.
        if (context.Configuration["Break"] is { } originRule && originRule.StartsWith("origin-", StringComparison.Ordinal))
        {
            contributions.AddSessionOrigin(originRule switch
            {
                "origin-handler" => new SessionOrigin("fine", typeof(string)),
                "origin-taken" => new SessionOrigin("hello", typeof(HelloOriginHandler)),
                _ => new SessionOrigin("Not-Valid", typeof(HelloOriginHandler)),
            });
            return;
        }

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
                "action-scan-session" => new HostAction("fine.scan", "Scan", "fine") { Scan = true },
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

/// <summary>Answers on the test plugin's session origin with the label it was given.</summary>
public sealed class HelloOriginHandler : ISessionOriginHandler
{
    public Task HandleAsync(HttpContext context, string label) => context.Response.WriteAsync($"hello origin {label}");
}

/// <summary>A plugin that contributes nothing: a dependant of hello, for the plugin list's <c>dependsOn</c> (SDK 1.8).</summary>
public sealed class QuietPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}
