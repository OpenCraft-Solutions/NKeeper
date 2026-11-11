using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NKeeper.Abstractions;
using NKeeper.Core.Access;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;
using NKeeper.Core.Trust;

namespace NKeeper.Core.Connectivity;

/// <summary>
/// Opens HTTP connections for plugins (docs/architecture.md §7.16), so certificate trust is core's. A certificate is
/// accepted when it's publicly valid, or when it's the one trusted for the host; core checks that with a handshake
/// before handing the connection over, and again on every request it sends.
/// </summary>
public sealed class HttpConnector(CoreDbContext db, TrustStore trust, IAuthSessionAccessor sessions, IHttpContextAccessor http)
{
    public const string CertificateAlgorithm = "x509";
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <exception cref="HttpConnectException">The certificate isn't trusted, or the service can't be reached.</exception>
    public async Task<IHttpConnection> OpenAsync(HostContext host, Guid serviceId, bool tls, CancellationToken cancellationToken)
    {
        var context = http.HttpContext;

        // Only for the host core authorized for this plugin request: a plugin could make up another.
        if (context is null || PluginCaller.Of(context) is null || !ReferenceEquals(context.Features.Get<HostContext>(), host))
        {
            throw new InvalidOperationException("HTTP connections can only be opened from a plugin's host-scoped endpoint, for its own host.");
        }

        var service = host.Services.FirstOrDefault(s => s.Id == serviceId);
        var stored = await db.HostServices.AsNoTracking().Where(s => s.Id == serviceId && s.HostId == host.Id).Select(s => new { s.Address }).FirstOrDefaultAsync(cancellationToken);
        if (service?.Address is null || stored is null)
        {
            throw new HttpConnectException(HttpConnectFailure.Unreachable, $"“{host.Name}” has no such service any more. Reload the host and try again.");
        }

        if (service.Port is not { } port)
        {
            throw new HttpConnectException(HttpConnectFailure.Unreachable, $"This service of “{host.Name}” has no port. Edit the host to set one.");
        }

        if (await db.Hosts.AnyAsync(h => h.Id == host.Id && h.JumpHostId != null, cancellationToken))
        {
            throw new HttpConnectException(HttpConnectFailure.JumpHostUnsupported, $"“{host.Name}” is reached through a jump host, which web connections can't use yet.");
        }

        var baseAddress = new UriBuilder(tls ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, service.Address, port).Uri;
        if (!tls)
        {
            return new HttpConnection(baseAddress, Handler(baseAddress, validate: null));
        }

        var endpoint = new TrustEndpoint(host.Id, serviceId, TrustedKeyKind.TlsCertificate, stored.Address ?? string.Empty, port);
        var known = await trust.KnownAsync(endpoint, cancellationToken);
        using var presented = await HandshakeAsync(service.Address, port, cancellationToken);
        if (!presented.PubliclyValid)
        {
            var fingerprint = Fingerprint(presented.Certificate);
            switch (TrustStore.Check(known, CertificateAlgorithm, fingerprint))
            {
                case TrustVerdict.Unknown:
                    trust.Presented(sessions.CurrentSessionKey(context), endpoint, CertificateAlgorithm, fingerprint);
                    throw new HttpConnectException(
                        HttpConnectFailure.HostKeyUnknown,
                        $"“{host.Name}” presented a certificate that isn't publicly valid, and no one has trusted it yet.",
                        new PresentedCertificate(host.Id, serviceId, fingerprint, presented.Certificate.Subject, presented.Certificate.NotAfter.ToUniversalTime()));
                case TrustVerdict.Changed:
                    var trusted = known.First(k => k.Algorithm == CertificateAlgorithm).Fingerprint;
                    await trust.MismatchAsync(endpoint, CertificateAlgorithm, trusted, fingerprint, CancellationToken.None);
                    throw new HttpConnectException(
                        HttpConnectFailure.HostKeyChanged,
                        $"“{host.Name}” presented a different certificate than the one trusted for it. Someone who manages the host must remove the old one first.");
            }
        }

        // Every later request: publicly valid, or the certificate trusted for the host, read once here.
        var trustedFingerprints = known.Where(k => k.Algorithm == CertificateAlgorithm).Select(k => k.Fingerprint).ToHashSet(StringComparer.Ordinal);
        if (!presented.PubliclyValid)
        {
            trustedFingerprints.Add(Fingerprint(presented.Certificate));
        }

        return new HttpConnection(baseAddress, Handler(baseAddress, (certificate, errors) =>
            errors == SslPolicyErrors.None || (certificate is not null && trustedFingerprints.Contains(Fingerprint(certificate)))));
    }

    /// <summary>A certificate's SHA-256, in unpadded base64 like an SSH key's.</summary>
    public static string Fingerprint(X509Certificate certificate) => Convert.ToBase64String(SHA256.HashData(certificate.GetRawCertData())).TrimEnd('=');

    /// <summary>The certificate a server presents, and whether it's publicly valid for <paramref name="address"/>.</summary>
    public static async Task<PresentedTls> HandshakeAsync(string address, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        X509Certificate2? certificate = null;
        var errors = SslPolicyErrors.None;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, timeout.Token);
            // This handshake only reads the certificate, and sends nothing over the connection: the caller decides
            // on the certificate afterwards, against the trust store, before any request is made.
#pragma warning disable CA5359 // Accepting any certificate: see above.
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, presented, _, policyErrors) =>
            {
                certificate = presented is null ? null : new X509Certificate2(presented);
                errors = policyErrors;
                return true;
            });
#pragma warning restore CA5359
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = address }, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            certificate?.Dispose();
            throw new HttpConnectException(HttpConnectFailure.TimedOut, $"{address}:{port} didn't answer within {ConnectTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            certificate?.Dispose();
            throw new HttpConnectException(HttpConnectFailure.Unreachable, $"Couldn't open a TLS connection to {address}:{port}: {ex.Message}");
        }

        return certificate is null
            ? throw new HttpConnectException(HttpConnectFailure.Unreachable, $"{address}:{port} presented no certificate.")
            : new PresentedTls(certificate, errors == SslPolicyErrors.None);
    }

    private static HttpMessageInvoker Handler(Uri baseAddress, Func<X509Certificate?, SslPolicyErrors, bool>? validate)
    {
        var sockets = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = ConnectTimeout,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = validate is null ? null : (_, certificate, _, errors) => validate(certificate, errors),
            },
        };
        return new HttpMessageInvoker(new OneEndpointHandler(baseAddress, sockets), disposeHandler: true);
    }

    /// <summary>Refuses any request to another scheme, host or port than the service's.</summary>
    private sealed class OneEndpointHandler(Uri baseAddress, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri is { IsAbsoluteUri: true } uri && Uri.Compare(uri, baseAddress, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
                ? base.SendAsync(request, cancellationToken)
                : throw new InvalidOperationException($"This connection only sends requests to {baseAddress.GetLeftPart(UriPartial.Authority)}.");
    }

    private sealed class HttpConnection(Uri baseAddress, HttpMessageInvoker client) : IHttpConnection
    {
        public HttpMessageInvoker Client { get; } = client;

        public Uri BaseAddress { get; } = baseAddress;

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A server's certificate from a handshake, and whether it was publicly valid.</summary>
public sealed record PresentedTls(X509Certificate2 Certificate, bool PubliclyValid) : IDisposable
{
    public void Dispose() => Certificate.Dispose();
}
