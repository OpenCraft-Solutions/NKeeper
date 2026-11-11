using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

namespace NKeeper.Testing;

/// <summary>
/// An xrdp server and Apache Guacamole's guacd, for desktop tests (docs/architecture.md §7.20). guacd shares the xrdp
/// container's network namespace, and xrdp listens on the very port published on the host, so
/// <c>127.0.0.1:<see cref="Port"/></c> reaches the same server from the server under test (on the host, for the
/// certificate check) and from guacd (in Docker, for the session). Ported from 1.x; the image is the test
/// testing project's <c>Rdp/xrdp</c>.
/// </summary>
public sealed class RdpServerFixture : IAsyncLifetime
{
    public const string UserName = "rdpuser";
    public const string Password = "Rdp-Test-Passw0rd";
    public const string Address = "127.0.0.1";
    private const int GuacdPort = 4822;

    private IContainer? _xrdp;
    private IContainer? _guacd;

    /// <summary>The RDP port, identical inside the containers and on the host.</summary>
    public int Port { get; private set; }

    /// <summary>Where guacd listens, on the host: for <c>Guacd:Address</c>.</summary>
    public string GuacdAddress => $"127.0.0.1:{_xrdp!.GetMappedPublicPort(GuacdPort)}";

    public async ValueTask InitializeAsync()
    {
        Port = FreeTcpPort();
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetProjectDirectory(), "Rdp/xrdp")
            .WithName("nkeeper-test-xrdp:latest")
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync();

        _xrdp = new ContainerBuilder(image)
            .WithEnvironment("XRDP_PORT", Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithPortBinding(Port, Port)
            .WithPortBinding(GuacdPort, assignRandomHostPort: true)
            .Build();
        await _xrdp.StartAsync();

        _guacd = new ContainerBuilder("guacamole/guacd:1.6.0")
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
                parameters.HostConfig.NetworkMode = $"container:{_xrdp.Id}";
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Listening on host"))
            .Build();
        await _guacd.StartAsync();
        await WaitForTcpAsync(Address, Port);
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

        if (_xrdp is not null)
        {
            await _xrdp.DisposeAsync();
        }
    }
}
