using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NKeeper.Abstractions;

namespace NKeeper.Core.Connectivity.Guacamole;

/// <summary>The server's TLS certificate and the security it agreed to.</summary>
/// <param name="Fingerprint">SHA-256 as colon-separated hex, the form guacd pins.</param>
/// <param name="TrustFingerprint">SHA-256 as unpadded base64, the form the trust store keeps (docs/architecture.md §7.7).</param>
public sealed record RdpServerIdentity(string GuacdSecurity, string Fingerprint, string TrustFingerprint, string Subject, DateTimeOffset NotAfter)
{
    /// <summary>The value guacd's <c>cert-fingerprints</c> parameter expects.</summary>
    public string PinnedFingerprint => $"sha256:{Fingerprint.ToLowerInvariant()}";
}

/// <summary>
/// Reads an RDP server's TLS certificate before connecting, the way a client starts any RDP
/// connection (MS-RDPBCGR 1.3.1.1): an X.224 Connection Request with an RDP negotiation
/// request, then a TLS handshake on the same socket. The certificate is then verified by the
/// user (trust on first use) and pinned in guacd, which refuses any other certificate.
/// </summary>
/// <remarks>Ported from 1.x.</remarks>
public static class RdpCertificateProbe
{
    private const uint ProtocolTls = 0x1;
    private const uint ProtocolNla = 0x2;
    private const uint ProtocolNlaExtended = 0x8;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <param name="address">The server's own address: the certificate is read for it.</param>
    /// <param name="dial">Where to dial instead, a jump host tunnel's end.</param>
    /// <exception cref="ConnectException">The server can't be reached, isn't an RDP server, or won't agree on security.</exception>
    public static async Task<RdpServerIdentity> ProbeAsync(string address, int port, DesktopSecurity mode, CancellationToken cancellationToken, HttpConnector.DialTo? dial = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(dial?.Address ?? address, dial?.Port ?? port, timeout.Token);
            await using var stream = client.GetStream();

            var requested = mode == DesktopSecurity.Tls ? ProtocolTls : ProtocolTls | ProtocolNla;
            await stream.WriteAsync(ConnectionRequest(requested), timeout.Token);
            var selected = ReadNegotiationResponse(await ReadTpktAsync(stream, timeout.Token), mode);

            using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = address,
                    // This connection only reads the certificate: nothing is sent over it and it is
                    // closed right away. Trust comes from the user confirming this exact certificate
                    // (RDP servers almost always use self-signed ones), which guacd then pins.
#pragma warning disable CA5359
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
#pragma warning restore CA5359
                },
                timeout.Token);

            using var certificate = new X509Certificate2(tls.RemoteCertificate!);
            var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            return new RdpServerIdentity(
                selected == ProtocolTls ? "tls" : "nla",
                string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2))),
                HttpConnector.Fingerprint(certificate),
                certificate.Subject,
                certificate.NotAfter.ToUniversalTime());
        }
        catch (SocketException ex)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"Couldn't connect to {address}:{port} ({ex.SocketErrorCode}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConnectException(ConnectFailure.TimedOut, $"Timed out connecting to {address}:{port}.");
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or EndOfStreamException)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"{address}:{port} didn't complete a secure Remote Desktop handshake. Is it an RDP server?");
        }
    }

    /// <summary>TPKT + X.224 Connection Request + RDP_NEG_REQ.</summary>
    private static byte[] ConnectionRequest(uint protocols)
    {
        var packet = new byte[19];
        packet[0] = 3; // TPKT version
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 19);
        packet[4] = 14; // X.224 length indicator
        packet[5] = 0xE0; // Connection Request
        packet[11] = 0x01; // TYPE_RDP_NEG_REQ
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(13), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(15), protocols);
        return packet;
    }

    private static async Task<byte[]> ReadTpktAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        if (header[0] != 3 || length is < 11 or > 512)
        {
            throw new ConnectException(ConnectFailure.Unreachable, "The server's reply isn't a Remote Desktop response.");
        }

        var packet = new byte[length];
        header.CopyTo(packet, 0);
        await stream.ReadExactlyAsync(packet.AsMemory(4), cancellationToken);
        return packet;
    }

    /// <summary>Checks the server's choice against the service's security setting.</summary>
    private static uint ReadNegotiationResponse(byte[] packet, DesktopSecurity mode)
    {
        if (packet[5] != 0xD0)
        {
            throw new ConnectException(ConnectFailure.Unreachable, "The server's reply isn't a Remote Desktop response.");
        }

        if (packet.Length < 19)
        {
            throw Insecure();
        }

        var type = packet[11];
        var value = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(15));
        if (type == 0x03)
        {
            throw value switch
            {
                // HYBRID_REQUIRED_BY_SERVER / SSL_WITH_USER_AUTH_REQUIRED_BY_SERVER
                5 or 6 => new ConnectException(ConnectFailure.SecurityNegotiationFailed,
                    "The server requires Network Level Authentication (NLA). Set this service's security to Automatic or NLA."),
                // SSL_NOT_ALLOWED_BY_SERVER / SSL_CERT_NOT_ON_SERVER
                2 or 3 => Insecure(),
                _ => new ConnectException(ConnectFailure.SecurityNegotiationFailed, $"The server refused the security negotiation (code {value})."),
            };
        }

        if (type != 0x02 || value == 0)
        {
            throw Insecure();
        }

        var nla = (value & (ProtocolNla | ProtocolNlaExtended)) != 0;
        if (mode == DesktopSecurity.Nla && !nla)
        {
            throw new ConnectException(ConnectFailure.SecurityNegotiationFailed,
                "The server doesn't offer Network Level Authentication (NLA). If that's expected (e.g. xrdp), set this service's security to TLS.");
        }

        return nla ? ProtocolNla : ProtocolTls;
    }

    private static ConnectException Insecure() => new(
        ConnectFailure.SecurityNegotiationFailed,
        "The server only offers legacy RDP security, which can't be verified and isn't supported. Enable TLS or NLA on the server.");
}
