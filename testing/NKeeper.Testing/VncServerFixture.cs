using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

namespace NKeeper.Testing;

/// <summary>
/// A VNC server (TigerVNC), password protected, and Apache Guacamole's guacd, for VNC tests (docs/architecture.md §7.20).
/// guacd shares the VNC container's network namespace, and it listens on the very port published on the host, so
/// <c>127.0.0.1:<see cref="Port"/></c> reaches the same server from the server under test and from guacd. Ported from
/// 1.x, on TigerVNC; the image is the testing project's <c>Vnc/tigervnc</c>.
/// </summary>
public sealed class VncServerFixture : IAsyncLifetime
{
    public const string Password = "vnc-pass";
    public const string Address = "127.0.0.1";
    private const int GuacdPort = 4822;

    private IContainer? _vnc;
    private IContainer? _guacd;

    /// <summary>The VNC port, identical inside the containers and on the host.</summary>
    public int Port { get; private set; }

    /// <summary>Where guacd listens, on the host: for <c>Guacd:Address</c>.</summary>
    public string GuacdAddress => $"127.0.0.1:{_vnc!.GetMappedPublicPort(GuacdPort)}";

    public async ValueTask InitializeAsync()
    {
        Port = FreeTcpPort();
        var image = new ImageFromDockerfileBuilder()
            // Copied beside the tests at build time, as the xrdp image is.
            .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "Vnc", "tigervnc"))
            .WithName("nkeeper-test-vnc:latest")
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync();

        _vnc = new ContainerBuilder(image)
            .WithEnvironment("VNC_PORT", Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("VNC_PASSWORD", Password)
            .WithPortBinding(Port, Port)
            .WithPortBinding(GuacdPort, assignRandomHostPort: true)
            .Build();
        await _vnc.StartAsync();

        _guacd = new ContainerBuilder("guacamole/guacd:1.6.0")
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
                parameters.HostConfig.NetworkMode = $"container:{_vnc.Id}";
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Listening on host"))
            .Build();
        await _guacd.StartAsync();
        await WaitForTcpAsync(Address, Port);
    }

    /// <summary>Connections to the VNC server now, from guacd: to check a closed session closed its connection.</summary>
    public async Task<int> OpenConnectionsAsync()
    {
        // /proc/net/tcp lists the port in hex; state 01 is ESTABLISHED.
        var port = Port.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
        var result = await _vnc!.ExecAsync(["sh", "-c", $"cat /proc/net/tcp /proc/net/tcp6 2>/dev/null | awk '{{print $2, $4}}' | grep -c ':{port} 01$' || true"]);
        return int.Parse(result.Stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForTcpAsync(string address, int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address, port);
                return;
            }
            catch (SocketException) when (attempt < 60)
            {
                await Task.Delay(500);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_guacd is not null)
        {
            await _guacd.DisposeAsync();
        }

        if (_vnc is not null)
        {
            await _vnc.DisposeAsync();
        }
    }
}
