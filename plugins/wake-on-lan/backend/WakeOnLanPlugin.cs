using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.WakeOnLan;

/// <summary>
/// Wakes hosts with a Wake-on-LAN magic packet (docs/architecture.md §7.15). The smallest real plugin: a service
/// type holding the MAC address, a run action, one host endpoint and one audit event. Ported from 1.x.
/// </summary>
public sealed class WakeOnLanPlugin : IPlugin
{
    public const string ServiceTypeId = "wake-on-lan";

    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        contributions.AddServiceType(new ServiceType(ServiceTypeId, "Wake-on-LAN")
        {
            DefaultPort = MagicPacket.DefaultPort,
            Settings = [ServiceSetting.Text("macAddress", "MAC address", maxLength: 17, required: true)],
        });
        contributions.AddHostAction(new HostAction("wake-on-lan.wake", "Wake")
        {
            Endpoint = "wake",
            ServiceTypes = [ServiceTypeId],
            Credential = HostActionCredential.None,
        });
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context) =>
        endpoints.MapHostGroup(HostPermission.Use).MapPost("/wake", WakeAsync);

    private static async Task<IResult> WakeAsync(HttpContext http, WakeRequest? request, IPluginAudit audit)
    {
        var host = http.GetHostContext();
        var service = host.Services.FirstOrDefault(s => s.Id == request?.ServiceId && s.Type == ServiceTypeId);
        if (service is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "This host has no such Wake-on-LAN service. Reload the host and try again.");
        }

        var mac = service.Settings.TryGetProperty("macAddress", out var value) ? value.GetString() : null;
        if (!MagicPacket.TryParseMac(mac, out var macBytes))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: $"“{mac}” isn’t a MAC address. Write it as six pairs of hexadecimal digits, such as 00:1a:2b:3c:4d:5e.");
        }

        var sent = await MagicPacket.SendAsync(macBytes, service.Address, service.Port ?? MagicPacket.DefaultPort, http.RequestAborted);
        var formatted = MagicPacket.Format(macBytes);
        await audit.RecordAsync("wake-on-lan.send", host.Id, $"{formatted} to {string.Join(", ", sent)}", http.RequestAborted);
        return sent.Count == 0
            ? Results.Problem(statusCode: StatusCodes.Status502BadGateway, detail: "The packet couldn't be sent: the server has no route for it.")
            : Results.Ok(new WakeResponse($"Sent a Wake-on-LAN packet to {formatted}. It can take a minute or two for {host.Name} to start."));
    }

    private sealed record WakeRequest(Guid ServiceId);

    private sealed record WakeResponse(string Message);
}

/// <summary>
/// The magic packet: six <c>0xFF</c> bytes, then the MAC address 16 times, over UDP. It goes to the local broadcast
/// address, and for an IPv4 address also to its /24 subnet's broadcast address and to the address itself, for
/// routers that forward directed broadcasts or keep a static ARP entry.
/// </summary>
public static class MagicPacket
{
    public const int DefaultPort = 9;

    public static byte[] Build(ReadOnlySpan<byte> mac)
    {
        var packet = new byte[6 + (16 * 6)];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var i = 0; i < 16; i++)
        {
            mac.CopyTo(packet.AsSpan(6 + (i * 6), 6));
        }

        return packet;
    }

    /// <summary>Six hexadecimal bytes, separated by <c>:</c> or <c>-</c>.</summary>
    public static bool TryParseMac(string? text, out byte[] mac)
    {
        mac = [];
        var parts = text?.Trim().Split(':', '-');
        if (parts is not { Length: 6 } || parts.Any(p => p.Length != 2))
        {
            return false;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        mac = bytes;
        return true;
    }

    public static string Format(ReadOnlySpan<byte> mac) => Convert.ToHexStringLower(mac).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => $"{a}:{b}");

    /// <returns>Where the packet went, for the audit trail.</returns>
    public static async Task<List<string>> SendAsync(byte[] mac, string? address, int port, CancellationToken cancellationToken)
    {
        var packet = Build(mac);
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        if (address is not null && IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = ip.GetAddressBytes();
            targets.Add(new IPAddress([octets[0], octets[1], octets[2], 255]));
            targets.Add(ip);
        }

        using var udp = new UdpClient { EnableBroadcast = true };
        var sent = new List<string>();
        foreach (var target in targets.Distinct())
        {
            try
            {
                await udp.SendAsync(packet, new IPEndPoint(target, port), cancellationToken);
                sent.Add($"{target}:{port}");
            }
            catch (SocketException)
            {
                // No route for a directed broadcast, say: the other targets may still work.
            }
        }

        return sent;
    }
}
