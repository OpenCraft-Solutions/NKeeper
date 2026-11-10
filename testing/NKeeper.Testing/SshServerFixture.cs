using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace NKeeper.Testing;

/// <summary>
/// A real OpenSSH server in a container, shared by a test class (docs/v2-plan.md §2.3: tests against real
/// servers). It takes <see cref="Password"/>, <see cref="PrivateKey"/> and <see cref="EncryptedPrivateKey"/>
/// (passphrase <see cref="Passphrase"/>) for <see cref="UserName"/>, and allows TCP forwarding, so it can
/// also be a jump host: its own sshd, at <see cref="InnerAddress"/>:<see cref="InnerPort"/>, is then a host
/// only it can reach.
/// </summary>
public sealed class SshServerFixture : IAsyncLifetime
{
    public const string UserName = "tester";
    public const string Password = "Ssh-Test-Passw0rd";
    public const string Passphrase = "Key-Passphr4se";

    /// <summary>Where the server listens inside its container, as seen through it as a jump host.</summary>
    public const string InnerAddress = "127.0.0.1";

    public const int InnerPort = 2222;

    private readonly IContainer _container = new ContainerBuilder("lscr.io/linuxserver/openssh-server:10.3_p1-r1-ls239")
        .WithEnvironment("PASSWORD_ACCESS", "true")
        .WithEnvironment("USER_NAME", UserName)
        .WithEnvironment("USER_PASSWORD", Password)
        .WithPortBinding(InnerPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(@"\[ls\.io-init\] done\."))
        .Build();

    /// <summary>The address tests connect to.</summary>
    public const string Address = "127.0.0.1";

    public int Port => _container.GetMappedPublicPort(InnerPort);

    /// <summary>An unencrypted OpenSSH ed25519 private key authorized for <see cref="UserName"/>.</summary>
    public string PrivateKey { get; private set; } = string.Empty;

    /// <summary>An authorized private key encrypted with <see cref="Passphrase"/>.</summary>
    public string EncryptedPrivateKey { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        PrivateKey = await KeyAsync("plain", string.Empty);
        EncryptedPrivateKey = await KeyAsync("encrypted", Passphrase);

        var forwarding = await _container.ExecAsync([
            "sh", "-c",
            "sed -i 's/^AllowTcpForwarding no/AllowTcpForwarding yes/' /config/sshd/sshd_config && s6-svc -r /run/service/svc-openssh-server",
        ]);
        if (forwarding.ExitCode != 0)
        {
            throw new InvalidOperationException($"Enabling TCP forwarding failed: {forwarding.Stderr}");
        }

        // The restart takes a moment; wait until the server answers again.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var probe = await _container.ExecAsync(["sh", "-c", $"nc -z 127.0.0.1 {InnerPort}"]);
            if (probe.ExitCode == 0)
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new InvalidOperationException("The SSH server didn't come back after enabling TCP forwarding.");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private async Task<string> KeyAsync(string name, string passphrase)
    {
        var path = $"/tmp/nkeeper_{name}";
        var result = await _container.ExecAsync([
            "sh", "-c",
            $"ssh-keygen -q -t ed25519 -N '{passphrase}' -f {path}"
            + $" && cat {path}.pub >> /config/.ssh/authorized_keys"
            + $" && chown {UserName} /config/.ssh/authorized_keys && chmod 600 /config/.ssh/authorized_keys"
            + $" && cat {path}",
        ]);
        return result.ExitCode == 0 ? result.Stdout : throw new InvalidOperationException($"Generating a test key failed: {result.Stderr}");
    }
}
