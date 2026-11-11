using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NKeeper.Abstractions;
using NKeeper.Core.Audit;
using NKeeper.Core.Connectivity.Guacamole;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;
using NKeeper.Core.Trust;

namespace NKeeper.Core.Connectivity;

/// <summary>
/// Remote desktops through guacd (docs/architecture.md §7.20). Core checks the handle and the server's certificate,
/// and does the Guacamole handshake itself with the credential, so a plugin never sees it and the browser can never
/// choose the protocol, target or parameters. The browser's instructions then pass core's allow-list.
/// </summary>
public sealed class DesktopConnector(
    CredentialBroker broker,
    CoreDbContext db,
    TrustStore trust,
    IAuthSessionAccessor sessions,
    IHttpContextAccessor http,
    IJumpTunnels tunnels,
    IAuditLog audit,
    IConfiguration configuration)
{
    public const string GuacdAddressSetting = "Guacd:Address";
    public const string DefaultGuacdAddress = "127.0.0.1:4822";

    /// <exception cref="DesktopConnectException">The certificate isn't trusted, guacd isn't running, or the server can't be reached.</exception>
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    public async Task<IDesktopConnection> OpenAsync(HostContext host, Guid serviceId, CredentialHandle handle, DesktopOptions options, CancellationToken cancellationToken)
    {
        Check(options);
        var (pluginId, _, entry) = await broker.UseAsync(host, serviceId, handle, cancellationToken);
        var material = entry.Material;
        if (string.IsNullOrEmpty(material.Username) || material.Password is null)
        {
            throw new DesktopConnectException(DesktopConnectFailure.NoSecret, "Remote desktops need a username and a password. Edit the credential, or type one.");
        }

        var service = host.Services.First(s => s.Id == serviceId);
        var stored = await db.HostServices.AsNoTracking().Where(s => s.Id == serviceId && s.HostId == host.Id).Select(s => new { s.Address }).FirstOrDefaultAsync(cancellationToken);
        if (service.Address is null || service.Port is not { } port || stored is null)
        {
            throw new DesktopConnectException(DesktopConnectFailure.Unreachable, $"This service of “{host.Name}” has no address or port. Edit the host to set them.");
        }

        IJumpTunnel? tunnel;
        try
        {
            tunnel = await tunnels.OpenAsync(host, service.Address, port, cancellationToken);
        }
        catch (HttpConnectException ex)
        {
            throw new DesktopConnectException(DesktopConnectFailure.JumpHostUnavailable, ex.Message);
        }

        GuacdConnection? guacd = null;
        try
        {
            var dial = tunnel is null ? null : new HttpConnector.DialTo("127.0.0.1", tunnel.LocalPort);
            var identity = await RdpCertificateProbe.ProbeAsync(service.Address, port, options.Security, cancellationToken, dial);
            await CheckTrustAsync(host, serviceId, new TrustEndpoint(host.Id, serviceId, TrustedKeyKind.TlsCertificate, stored.Address ?? string.Empty, port), identity, cancellationToken);

            var (guacdHost, guacdPort) = GuacdAddress();
            guacd = await GuacdConnection.OpenAsync(guacdHost, guacdPort, cancellationToken);
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hostname"] = dial?.Address ?? service.Address,
                ["port"] = (dial?.Port ?? port).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["username"] = material.Username,
                ["password"] = material.Password.Reveal(),
                ["domain"] = material.Domain ?? string.Empty,
                ["security"] = identity.GuacdSecurity,

                // guacd accepts exactly the certificate just checked against the trust store.
                ["cert-fingerprints"] = identity.PinnedFingerprint,
                ["resize-method"] = "display-update",
            };
            var ready = await guacd.HandshakeAsync(
                "rdp",
                new GuacdClientInfo(options.Width, options.Height, options.Dpi, options.AudioMimetypes, options.ImageMimetypes, options.Timezone),
                values,
                cancellationToken);
            var credential = material.Source == CredentialSource.Saved ? $"credential {material.CredentialId}" : "a typed credential";
            await audit.RecordAsync(new AuditEntry(AuditActions.CredentialUse, host.Id.ToString(), $"rdp to service {serviceId}, plugin {pluginId}, {credential}"), cancellationToken);
            return new DesktopConnection(guacd, ready, tunnel);
        }
        catch
        {
            if (guacd is not null)
            {
                await guacd.DisposeAsync();
            }

            tunnel?.Dispose();
            throw;
        }
    }

    private async Task CheckTrustAsync(HostContext host, Guid serviceId, TrustEndpoint endpoint, RdpServerIdentity identity, CancellationToken cancellationToken)
    {
        var known = await trust.KnownAsync(endpoint, cancellationToken);
        switch (TrustStore.Check(known, HttpConnector.CertificateAlgorithm, identity.TrustFingerprint))
        {
            case TrustVerdict.Unknown:
                trust.Presented(sessions.CurrentSessionKey(http.HttpContext!), endpoint, HttpConnector.CertificateAlgorithm, identity.TrustFingerprint);
                throw new DesktopConnectException(
                    DesktopConnectFailure.HostKeyUnknown,
                    $"“{host.Name}” presented a Remote Desktop certificate no one has trusted yet.",
                    new PresentedCertificate(host.Id, serviceId, identity.TrustFingerprint, identity.Subject, identity.NotAfter));
            case TrustVerdict.Changed:
                var trusted = known.First(k => k.Algorithm == HttpConnector.CertificateAlgorithm).Fingerprint;
                await trust.MismatchAsync(endpoint, HttpConnector.CertificateAlgorithm, trusted, identity.TrustFingerprint, CancellationToken.None);
                throw new DesktopConnectException(
                    DesktopConnectFailure.HostKeyChanged,
                    $"“{host.Name}” presented a different Remote Desktop certificate than the one trusted for it. Someone who manages the host must remove the old one first.");
        }
    }

    private (string Host, int Port) GuacdAddress()
    {
        var value = configuration[GuacdAddressSetting] ?? DefaultGuacdAddress;
        var colon = value.LastIndexOf(':');
        return colon > 0 && int.TryParse(value.AsSpan(colon + 1), System.Globalization.CultureInfo.InvariantCulture, out var port)
            ? (value[..colon], port)
            : throw new InvalidOperationException($"{GuacdAddressSetting} must be host:port, such as {DefaultGuacdAddress}.");
    }

    private static void Check(DesktopOptions options)
    {
        static bool Mime(string m) => m.Length is > 0 and <= 64 && m.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '.' or '+' or ';' or '=');
        if (options.Width is < 200 or > 8192 || options.Height is < 200 or > 8192 || options.Dpi is < 48 or > 480
            || options.AudioMimetypes.Count > 8 || options.ImageMimetypes.Count > 8
            || !options.AudioMimetypes.All(Mime) || !options.ImageMimetypes.All(Mime)
            || options.Timezone is { Length: > 64 } || (options.Timezone is { } zone && !zone.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+')))
        {
            throw new ArgumentException("The desktop's size must be between 200 × 200 and 8192 × 8192, at 48 to 480 DPI, with at most 8 plain media types and a time zone name.", nameof(options));
        }
    }

    /// <summary>
    /// After the handshake: guacd's instructions out, and the browser's in through 1.x's allow-list
    /// (<see cref="GuacamoleInstructionFilter"/>), so no plugin can pass the browser anything else.
    /// </summary>
    private sealed class DesktopConnection(GuacdConnection guacd, GuacamoleInstruction ready, IJumpTunnel? tunnel) : IDesktopConnection
    {
        private readonly GuacamoleInstructionFilter _filter = new();
        private readonly GuacamoleParser _browser = new(1024 * 1024);
        private GuacamoleInstruction? _ready = ready;

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _ready, null) is { } first)
            {
                return first.Raw;
            }

            var next = await guacd.ReadInstructionAsync(cancellationToken);
            if (next is null)
            {
                return null;
            }

            // Whatever else already arrived goes with it, in one message.
            var text = new StringBuilder(next.Raw);
            foreach (var buffered in guacd.DrainBuffered())
            {
                text.Append(buffered.Raw);
            }

            return text.ToString();
        }

        public async Task SendFromBrowserAsync(string instructions, CancellationToken cancellationToken)
        {
            _browser.Append(instructions);
            var allowed = new StringBuilder();
            while (_browser.TryRead(out var instruction))
            {
                if (_filter.IsAllowed(instruction))
                {
                    allowed.Append(instruction.Raw);
                }
            }

            if (allowed.Length > 0)
            {
                await guacd.WriteAsync(allowed.ToString(), cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await guacd.DisposeAsync();
            tunnel?.Dispose();
        }
    }
}
