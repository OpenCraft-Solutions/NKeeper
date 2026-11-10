using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Ssh;

/// <summary>
/// The SSH plugin (docs/architecture.md §7.11): the <c>ssh</c> service type, and terminal sessions on it. Core
/// opens the connection, checks the server's key and signs in; this plugin only carries the terminal.
/// </summary>
public sealed class SshPlugin : IPlugin
{
    public const string ServiceTypeId = "ssh";

    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        contributions.AddServiceType(new ServiceType(ServiceTypeId, "SSH") { DefaultPort = 22 });
        contributions.AddSessionType(new SessionType(ServiceTypeId, "Terminal", typeof(TerminalSessionHandler)) { ServiceTypes = [ServiceTypeId] });
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context) =>
        services.AddScoped<TerminalSessionHandler>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}

/// <summary>Opens a terminal: a shell over SSH on an <c>xterm-256color</c> terminal of the size the browser asks for.</summary>
public sealed class TerminalSessionHandler(IConnectivity connectivity) : IStreamSessionHandler
{
    public const int MinSize = 1;
    public const int MaxSize = 1000;

    public async Task<IStreamSession> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Credential is null)
        {
            // Core reports this like any other failure to open; the shell always sends a credential.
            throw new SshConnectException(SshConnectFailure.NoSecret, "Choose a credential, or type one, to open a terminal.");
        }

        var terminal = new SshTerminal(
            Columns: Size(request.Options, "columns", 80),
            Rows: Size(request.Options, "rows", 24));
        var ssh = await connectivity.OpenSshAsync(request.Host, request.ServiceId, request.Credential, cancellationToken);
        try
        {
            return new TerminalSession(ssh, await ssh.OpenShellAsync(terminal, cancellationToken));
        }
        catch
        {
            await ssh.DisposeAsync();
            throw;
        }
    }

    /// <summary>A terminal dimension from the options, kept within bounds.</summary>
    internal static int Size(JsonElement options, string name, int fallback) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) && value.TryGetInt32(out var size)
            ? Math.Clamp(size, MinSize, MaxSize)
            : fallback;
}

/// <summary>
/// A terminal: binary frames carry its bytes both ways, and a text frame <c>{"type":"resize","columns":…,"rows":…}</c>
/// resizes it. It ends when the shell exits or the browser goes.
/// </summary>
internal sealed class TerminalSession(ISshConnection ssh, ISshShell shell) : IStreamSession
{
    public async Task RunAsync(IStreamChannel channel, CancellationToken cancellationToken)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // A shell stream's reads may not heed cancellation; closing it ends them.
        using var close = done.Token.Register(() => shell.Stream.Dispose());
        var output = PumpOutputAsync(channel, done.Token);
        var input = PumpInputAsync(channel, done.Token);

        // Whichever side finishes first (the shell exited, or the browser went) ends the other.
        await Task.WhenAny(output, input);
        await done.CancelAsync();
        await Quietly(output);
        await Quietly(input);
    }

    public async ValueTask DisposeAsync()
    {
        await shell.DisposeAsync();
        await ssh.DisposeAsync();
    }

    private async Task PumpOutputAsync(IStreamChannel channel, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await shell.Stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await channel.SendAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private async Task PumpInputAsync(IStreamChannel channel, CancellationToken cancellationToken)
    {
        while (await channel.ReceiveAsync(cancellationToken) is { } frame)
        {
            if (!frame.IsText)
            {
                await shell.Stream.WriteAsync(frame.Data, cancellationToken);
                await shell.Stream.FlushAsync(cancellationToken);
            }
            else if (Resize(frame.Data.Span) is var (columns, rows))
            {
                shell.Resize(columns, rows);
            }
        }
    }

    /// <summary>The size a <c>resize</c> message asks for; null for any other message.</summary>
    internal static (int Columns, int Rows)? Resize(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "resize"
                ? (TerminalSessionHandler.Size(root, "columns", 80), TerminalSessionHandler.Size(root, "rows", 24))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The session is ending.
        }
    }
}
