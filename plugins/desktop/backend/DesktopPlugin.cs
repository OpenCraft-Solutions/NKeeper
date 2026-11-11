using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Desktop;

/// <summary>
/// Remote desktops (docs/architecture.md §7.21): RDP through guacd, as a stream session. Core makes the connection
/// and filters the browser's instructions (§7.20); this plugin relays between them. Ported from 1.x's Desktop/.
/// </summary>
public sealed class DesktopPlugin : IPlugin
{
    public const string ServiceTypeId = "rdp";

    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        contributions.AddServiceType(new ServiceType(ServiceTypeId, "RDP")
        {
            DefaultPort = 3389,
            Health = HealthProbe.Tls,
            Settings = [ServiceSetting.Choice("security", "Security", ["any", "nla", "tls"])],
        });
        contributions.AddSessionType(new SessionType(ServiceTypeId, "Desktop", typeof(DesktopSessionHandler)) { ServiceTypes = [ServiceTypeId] });
        contributions.AddHostAction(new HostAction("rdp.desktop", "Desktop", ServiceTypeId));
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context) =>
        services.AddScoped<DesktopSessionHandler>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}

/// <summary>Opens the desktop through core, at the browser's size: options <c>{ width, height, dpi, timezone }</c>.</summary>
public sealed class DesktopSessionHandler(IConnectivity connectivity) : IStreamSessionHandler
{
    public async Task<IStreamSession> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken)
    {
        if (request.Credential is not { } credential)
        {
            throw new ConnectException(ConnectFailure.NoSecret, "Choose a credential, or type one, to open a desktop.");
        }

        var service = request.Host.Services.First(s => s.Id == request.ServiceId);
        var security = service.Settings.TryGetProperty("security", out var value) ? value.GetString() : null;
        var options = new DesktopOptions(
            Width: Number(request.Options, "width", 1280),
            Height: Number(request.Options, "height", 800),
            Dpi: Number(request.Options, "dpi", 96))
        {
            Security = security switch { "nla" => DesktopSecurity.Nla, "tls" => DesktopSecurity.Tls, _ => DesktopSecurity.Any },
            Timezone = request.Options.ValueKind == JsonValueKind.Object && request.Options.TryGetProperty("timezone", out var zone) && zone.ValueKind == JsonValueKind.String ? zone.GetString() : null,
        };
        return new DesktopSession(await connectivity.OpenDesktopAsync(request.Host, request.ServiceId, credential, options, cancellationToken));
    }

    /// <summary>A whole number from the options, kept in core's bounds.</summary>
    private static int Number(JsonElement options, string name, int fallback) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? Math.Clamp(number, name == "dpi" ? 48 : 200, name == "dpi" ? 480 : 8192)
            : fallback;
}

/// <summary>Relays Guacamole text: guacd's instructions to the browser, the browser's through core's allow-list.</summary>
internal sealed class DesktopSession(IDesktopConnection desktop) : IStreamSession
{
    public async Task RunAsync(IStreamChannel channel, CancellationToken cancellationToken)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var toBrowser = ToBrowserAsync(channel, done.Token);
        var fromBrowser = FromBrowserAsync(channel, done.Token);
        await Task.WhenAny(toBrowser, fromBrowser);
        await done.CancelAsync();
        await Task.WhenAll(toBrowser, fromBrowser).ContinueWith(_ => { }, TaskScheduler.Default);
        await Task.WhenAny(toBrowser, fromBrowser).Unwrap();
    }

    private async Task ToBrowserAsync(IStreamChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            while (await desktop.ReceiveAsync(cancellationToken) is { } instructions)
            {
                await channel.SendTextAsync(instructions, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // guacd closed the connection.
        }
    }

    private async Task FromBrowserAsync(IStreamChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            while (await channel.ReceiveAsync(cancellationToken) is { } frame)
            {
                if (frame.IsText)
                {
                    await desktop.SendFromBrowserAsync(Encoding.UTF8.GetString(frame.Data.Span), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (FormatException)
        {
            // Not Guacamole instructions: the session ends.
        }
    }

    public ValueTask DisposeAsync() => desktop.DisposeAsync();
}
