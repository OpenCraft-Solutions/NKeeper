using System.Text.Json;
using System.Text.Json.Serialization;

namespace NKeeper.Abstractions;

/// <summary>
/// Resolves the credential for one service of a host, for the plugin whose endpoint is running, and hands
/// out an opaque <see cref="CredentialHandle"/>, never the secret (SDK 0.1.3, docs/architecture.md §7.6).
/// </summary>
/// <remarks>
/// Every method takes the <see cref="HostContext"/> core gave this request (a host group's endpoint has
/// it), and refuses any other, or a call from outside a plugin endpoint, with <see cref="CredentialDeniedException"/>.
/// </remarks>
public interface ICredentialBroker
{
    /// <summary>A credential saved on the host, which must be used with <paramref name="serviceId"/>.</summary>
    Task<CredentialHandle> ResolveSavedAsync(HostContext host, Guid serviceId, Guid credentialId, CancellationToken cancellationToken = default);

    /// <summary>A credential the user typed into core's prompt; <paramref name="promptReference"/> is single-use.</summary>
    Task<CredentialHandle> ResolvePromptAsync(HostContext host, Guid serviceId, string promptReference, CancellationToken cancellationToken = default);

    /// <summary>What a handle refers to, without its secrets: for messages such as "connecting as root".</summary>
    Task<CredentialDescription> DescribeAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default);
}

/// <summary>
/// Refers to a resolved credential, which core keeps. Bound to the user, host, service, plugin and request
/// it was resolved for. It has nothing to read, prints as a placeholder, and can't be serialized.
/// </summary>
[JsonConverter(typeof(CredentialHandleJsonConverter))]
public sealed class CredentialHandle
{
    internal CredentialHandle(string token) => Token = token;

    internal string Token { get; }

    public override string ToString() => "CredentialHandle(***)";
}

public enum CredentialSource
{
    /// <summary>A credential saved on the host.</summary>
    Saved,

    /// <summary>One the user typed into core's prompt.</summary>
    Prompt,
}

/// <summary>A resolved credential, without its secrets.</summary>
/// <param name="CredentialId">The saved credential's ID; null for a typed one.</param>
public sealed record CredentialDescription(CredentialSource Source, Guid? CredentialId, string? Username, string? Domain, bool HasPassword, bool HasPrivateKey);

public enum CredentialDenial
{
    /// <summary>The host has no such credential.</summary>
    NotFound,

    /// <summary>The credential isn't used with the service, or the host has no such service.</summary>
    NotForService,

    /// <summary>The prompt reference is unknown, used, expired, or for another session, host or service.</summary>
    PromptInvalid,

    /// <summary>The handle is unknown or expired, or was resolved for another user, host, service, plugin or request.</summary>
    HandleInvalid,

    /// <summary>Not called from a plugin's own endpoint, or with a host context core didn't give this request.</summary>
    NotAllowed,
}

/// <summary>The broker refused. Core has audited it (<c>credential.denied</c>).</summary>
public sealed class CredentialDeniedException(CredentialDenial reason, string message) : Exception(message)
{
    public CredentialDenial Reason { get; } = reason;
}

/// <summary>Refuses to write or read a handle as JSON, so one can't reach a response or a log by accident.</summary>
public sealed class CredentialHandleJsonConverter : JsonConverter<CredentialHandle>
{
    public override CredentialHandle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("Credential handles can't be read from JSON.");

    public override void Write(Utf8JsonWriter writer, CredentialHandle value, JsonSerializerOptions options) =>
        throw new JsonException("Credential handles can't be written as JSON.");
}
