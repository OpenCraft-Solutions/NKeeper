using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace NKeeper.Core.Connectivity.Rpc;

/// <summary>Login for an authenticated RPC bind. Supplied when binding, not when connecting.</summary>
internal sealed record RpcCredentials(string Username, string Password, string? Domain);

/// <summary>An RPC fault (the server's status code) or a malformed or refused exchange.</summary>
internal sealed class RpcException(string message, uint? status = null) : Exception(message)
{
    public uint? Status { get; } = status;
}

/// <summary>
/// Ported from 1.x (docs/architecture.md §7.28). A minimal connection-oriented DCE/RPC (MS-RPCE, "ncacn_ip_tcp") client: one connection, one
/// interface bound at a time, synchronous calls with opaque NDR stubs. Authentication is NTLM
/// at the "connect" level, so it proves the login at bind time but does not sign or encrypt calls.
/// </summary>
internal sealed class RpcClient : IAsyncDisposable
{
    public const int EndpointMapperPort = 135;

    // On Linux, .NET's NTLM otherwise needs GSSAPI's NTLM module (gss-ntlmssp), which servers rarely have. Its own
    // managed NTLM needs nothing; it's read when NTLM is first used, so it's set before any bind.
    static RpcClient() => AppContext.SetSwitch("System.Net.Security.UseManagedNtlm", true);

    private const byte PtypeRequest = 0, PtypeResponse = 2, PtypeFault = 3, PtypeBind = 11, PtypeBindAck = 12, PtypeBindNak = 13, PtypeAuth3 = 16;
    private const byte FlagFirst = 0x01, FlagLast = 0x02;
    private const byte AuthTypeWinNt = 10, AuthLevelConnect = 2;
    private const int HeaderLength = 16, RequestHeaderLength = 24, MaxFragment = 5840, MaxResponseBytes = NKeeper.Abstractions.IRpcConnection.MaxResponseBytes;

    // NDR 2.0 transfer syntax: 8a885d04-1ceb-11c9-9fe8-08002b104860 v2.0.
    private static readonly Guid NdrSyntax = new("8a885d04-1ceb-11c9-9fe8-08002b104860");

    private readonly TcpClient _tcp = new();
    private Stream? _stream;
    private uint _callId;
    private int _maxFragment = MaxFragment;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        await _tcp.ConnectAsync(host, port, cancellationToken);
        _stream = _tcp.GetStream();
    }

    /// <summary>Binds an interface (abstract syntax) on the connection, authenticating when credentials are given.</summary>
    public async Task BindAsync(Guid interfaceId, ushort major, ushort minor, RpcCredentials? credentials, string targetHost, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        var callId = ++_callId;

        NegotiateAuthentication? auth = null;
        try
        {
            byte[]? token = null;
            if (credentials is not null)
            {
                auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
                {
                    Package = "NTLM",
                    Credential = new NetworkCredential(credentials.Username, credentials.Password, credentials.Domain ?? string.Empty),
                    TargetName = $"host/{targetHost}",
                    RequiredProtectionLevel = ProtectionLevel.None,
                });
                token = auth.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out var status);
                if (token is null || status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
                {
                    throw new RpcException($"Couldn't start NTLM authentication ({status}).");
                }
            }

            await stream.WriteAsync(BuildBind(callId, interfaceId, major, minor, token), cancellationToken);
            var ack = await ReadPduAsync(stream, cancellationToken);
            if (ack.Type == PtypeBindNak)
            {
                throw new RpcException("The server refused the bind.");
            }

            if (ack.Type != PtypeBindAck)
            {
                throw new RpcException("Unexpected reply to the bind.");
            }

            ParseBindAck(ack, out var maxFragment);
            _maxFragment = Math.Clamp(maxFragment, 1024, MaxFragment);

            if (auth is not null)
            {
                var challenge = ack.AuthValue ?? throw new RpcException("The server didn't offer authentication.");
                var response = auth.GetOutgoingBlob(challenge, out var status);
                if (response is null || status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
                {
                    throw new RpcException("The server rejected the login.");
                }

                await stream.WriteAsync(BuildAuth3(callId, response), cancellationToken);
            }
        }
        finally
        {
            auth?.Dispose();
        }
    }

    /// <summary>Calls operation <paramref name="opnum"/> of the bound interface and returns the response stub.</summary>
    public async Task<byte[]> CallAsync(ushort opnum, ReadOnlyMemory<byte> stub, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        var callId = ++_callId;
        var chunk = _maxFragment - RequestHeaderLength;

        // A request larger than one fragment is split; the first and last carry the matching flags.
        var offset = 0;
        do
        {
            var length = Math.Min(chunk, stub.Length - offset);
            var flags = (byte)((offset == 0 ? FlagFirst : 0) | (offset + length == stub.Length ? FlagLast : 0));
            await stream.WriteAsync(BuildRequest(callId, opnum, flags, (uint)stub.Length, stub.Span.Slice(offset, length)), cancellationToken);
            offset += length;
        }
        while (offset < stub.Length);

        using var response = new MemoryStream();
        while (true)
        {
            var pdu = await ReadPduAsync(stream, cancellationToken);
            switch (pdu.Type)
            {
                case PtypeFault:
                    throw new RpcException("The server reported a fault.", pdu.Body.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(pdu.Body.AsSpan(8)) : null);
                case PtypeResponse when pdu.Body.Length >= 8:
                    response.Write(pdu.Body, 8, pdu.Body.Length - 8);
                    if (response.Length > MaxResponseBytes)
                    {
                        throw new RpcException("The response is too large.");
                    }

                    if ((pdu.Flags & FlagLast) != 0)
                    {
                        return response.ToArray();
                    }

                    break;
                default:
                    throw new RpcException("Unexpected reply to the call.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync();
        }

        _tcp.Dispose();
    }

    private static byte[] BuildBind(uint callId, Guid interfaceId, ushort major, ushort minor, byte[]? authToken)
    {
        var body = new byte[8 + 4 + 44];
        BinaryPrimitives.WriteUInt16LittleEndian(body, MaxFragment);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), MaxFragment);
        // body[4..8] association group 0: a new one.
        body[8] = 1; // one presentation context
        // body[12..]: ctx id 0, one transfer syntax.
        body[14] = 1;
        interfaceId.TryWriteBytes(body.AsSpan(16), bigEndian: false, out _);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(32), major);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(34), minor);
        NdrSyntax.TryWriteBytes(body.AsSpan(36), bigEndian: false, out _);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(52), 2);

        return Pdu(PtypeBind, FlagFirst | FlagLast, callId, body, authToken);
    }

    private static byte[] BuildAuth3(uint callId, byte[] authToken) =>
        Pdu(PtypeAuth3, FlagFirst | FlagLast, callId, new byte[4], authToken);

    private static byte[] BuildRequest(uint callId, ushort opnum, byte flags, uint allocHint, ReadOnlySpan<byte> stub)
    {
        var body = new byte[RequestHeaderLength - HeaderLength + stub.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, allocHint);
        // body[4..6] context id 0.
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), opnum);
        stub.CopyTo(body.AsSpan(8));
        return Pdu(PtypeRequest, flags, callId, body, authToken: null);
    }

    /// <summary>Common header, body, then an NTLM auth verifier (padded to 4 bytes) when there is a token.</summary>
    private static byte[] Pdu(byte type, byte flags, uint callId, byte[] body, byte[]? authToken)
    {
        var pad = authToken is null ? 0 : (4 - (HeaderLength + body.Length) % 4) % 4;
        var authLength = authToken?.Length ?? 0;
        var total = HeaderLength + body.Length + (authToken is null ? 0 : pad + 8 + authLength);

        var pdu = new byte[total];
        pdu[0] = 5; // version 5.0
        pdu[2] = type;
        pdu[3] = flags;
        pdu[4] = 0x10; // little-endian, ASCII, IEEE floats
        BinaryPrimitives.WriteUInt16LittleEndian(pdu.AsSpan(8), (ushort)total);
        BinaryPrimitives.WriteUInt16LittleEndian(pdu.AsSpan(10), (ushort)authLength);
        BinaryPrimitives.WriteUInt32LittleEndian(pdu.AsSpan(12), callId);
        body.CopyTo(pdu, HeaderLength);

        if (authToken is not null)
        {
            var verifier = HeaderLength + body.Length + pad;
            pdu[verifier] = AuthTypeWinNt;
            pdu[verifier + 1] = AuthLevelConnect;
            pdu[verifier + 2] = (byte)pad;
            authToken.CopyTo(pdu, verifier + 8);
        }

        return pdu;
    }

    private static async Task<ReceivedPdu> ReadPduAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (header[0] != 5 || header[4] != 0x10)
        {
            throw new RpcException("The server didn't answer with DCE/RPC.");
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
        var authLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
        if (length < HeaderLength)
        {
            throw new RpcException("The server sent a malformed reply.");
        }

        var body = new byte[length - HeaderLength];
        await stream.ReadExactlyAsync(body, cancellationToken);

        // An auth verifier is 8 bytes of header plus the token, at the very end.
        byte[]? authValue = null;
        var trailer = authLength == 0 ? 0 : authLength + 8;
        if (trailer > body.Length)
        {
            throw new RpcException("The server sent a malformed reply.");
        }

        if (authLength > 0)
        {
            authValue = body[^authLength..];
        }

        return new ReceivedPdu(header[2], header[3], body[..(body.Length - trailer)], authValue);
    }

    private static void ParseBindAck(ReceivedPdu ack, out int maxFragment)
    {
        var body = ack.Body;
        if (body.Length < 10)
        {
            throw new RpcException("The server sent a malformed bind reply.");
        }

        maxFragment = BinaryPrimitives.ReadUInt16LittleEndian(body);
        var addressLength = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(8));

        // Results start after the secondary address, aligned to 4 from the start of the PDU.
        var results = (HeaderLength + 10 + addressLength + 3) / 4 * 4 - HeaderLength;
        if (body.Length < results + 4 + 24 || body[results] < 1)
        {
            throw new RpcException("The server sent a malformed bind reply.");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(results + 4)) != 0)
        {
            throw new RpcException("The server doesn't offer that interface.");
        }
    }

    private sealed record ReceivedPdu(byte Type, byte Flags, byte[] Body, byte[]? AuthValue);
}
