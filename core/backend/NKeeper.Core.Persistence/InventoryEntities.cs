namespace NKeeper.Core.Persistence;

/// <summary>
/// A machine or device that can be connected to (docs/architecture.md §7.1). Hosts aren't kept in
/// folders: the shell groups them by tag. Services, credentials and the rest join in later steps.
/// </summary>
public sealed class Host
{
    public const int NameMaxLength = 200;
    public const int HostnameMaxLength = 253;
    public const int IpAddressMaxLength = 45;
    public const int DescriptionMaxLength = 4000;
    public const int MaxTags = 100;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>A DNS name. At least one of <see cref="Hostname"/> and <see cref="IpAddress"/> is set.</summary>
    public string? Hostname { get; set; }

    /// <summary>An IPv4 or IPv6 address, in canonical text form.</summary>
    public string? IpAddress { get; set; }

    public string? Description { get; set; }

    public List<HostTag> Tags { get; set; } = [];

    /// <summary>In <see cref="HostService.Position"/> order once loaded through the inventory.</summary>
    public List<HostService> Services { get; set; } = [];

    /// <summary>In <see cref="HostCredential.Position"/> order once loaded through the inventory.</summary>
    public List<HostCredential> Credentials { get; set; } = [];

    /// <summary>
    /// The host this one is reached through (docs/architecture.md §7.8). It can't have a jump host of its own.
    /// Cleared when it's deleted.
    /// </summary>
    public Guid? JumpHostId { get; set; }

    /// <summary>The jump host's service to connect to it on. Null once that service is removed.</summary>
    public Guid? JumpServiceId { get; set; }

    /// <summary>The jump host's credential to sign in to it with. Null once that credential is removed.</summary>
    public Guid? JumpCredentialId { get; set; }

    public List<HostTrustedKey> TrustedKeys { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    /// <summary>Optimistic concurrency: PostgreSQL's <c>xmin</c>. Every edit updates the host row, so it changes with any part of the host.</summary>
    public uint Version { get; set; }
}

/// <summary>A tag: a name with an optional value, e.g. <c>env: production</c>. One name may appear with several values.</summary>
public sealed class HostTag
{
    public const int NameMaxLength = 64;
    public const int ValueMaxLength = 255;

    public Guid Id { get; set; }

    public Guid HostId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Empty for a tag without a value, so (host, name, value) can be unique.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// A service a host offers (docs/architecture.md §7.4), of a type a plugin contributes. Its ID stays the
/// same across edits, since credentials refer to it.
/// </summary>
public sealed class HostService
{
    public const int MaxPerHost = 50;
    public const int TypeMaxLength = 32;
    public const int LabelMaxLength = 100;

    public Guid Id { get; set; }

    public Guid HostId { get; set; }

    /// <summary>Its place in the host's list, from 0.</summary>
    public int Position { get; set; }

    /// <summary>The service type's ID. Kept when no enabled plugin provides it any more.</summary>
    public string Type { get; set; } = string.Empty;

    public string? Label { get; set; }

    /// <summary>Null for the type's default port.</summary>
    public int? Port { get; set; }

    /// <summary>A hostname or IP address, when the service listens somewhere other than the host; null for the host's own.</summary>
    public string? Address { get; set; }

    /// <summary>A JSON object, valid for the type's schema at <see cref="SettingsVersion"/>.</summary>
    public string Settings { get; set; } = "{}";

    public int SettingsVersion { get; set; } = 1;
}

/// <summary>
/// Login details for one host (docs/architecture.md §7.5). Secrets are stored only encrypted with the key
/// ring, bound to the credential's ID, and never returned by the API.
/// </summary>
public sealed class HostCredential
{
    public const int MaxPerHost = 50;
    public const int LabelMaxLength = 100;
    public const int UsernameMaxLength = 256;
    public const int DomainMaxLength = 256;
    public const int PasswordMaxLength = 1024;
    public const int PrivateKeyMaxLength = 16_384;
    public const int NotesMaxLength = 2000;

    public Guid Id { get; set; }

    public Guid HostId { get; set; }

    /// <summary>Its place in the host's list, from 0.</summary>
    public int Position { get; set; }

    public string? Label { get; set; }

    public string? Username { get; set; }

    public string? Domain { get; set; }

    /// <summary>The encrypted password, also the private key's passphrase. Null when not set.</summary>
    public byte[]? ProtectedPassword { get; set; }

    /// <summary>The encrypted private key. Null when not set.</summary>
    public byte[]? ProtectedPrivateKey { get; set; }

    public string? Notes { get; set; }

    /// <summary>The host's services this credential is used with; null for all of them. IDs of services removed since are ignored.</summary>
    public List<Guid>? ServiceIds { get; set; }
}

/// <summary>
/// Access for a user who isn't an administrator (§7.2): to one host, or to every host in a group,
/// a tag name with an optional value. Group grants follow the tags.
/// </summary>
public sealed class HostAccessGrant
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Set for a grant to one host; otherwise <see cref="TagName"/> is.</summary>
    public Guid? HostId { get; set; }

    /// <summary>Set for a group grant. Compared without case.</summary>
    public string? TagName { get; set; }

    /// <summary>For a group grant: the tag value to match, or null for any value.</summary>
    public string? TagValue { get; set; }

    public HostAccessLevel Level { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }
}

public enum HostAccessLevel
{
    /// <summary>See the host and connect to it.</summary>
    Use,

    /// <summary>Also change and delete it.</summary>
    Manage,
}

/// <summary>What kind of identity a trusted key is (docs/architecture.md §7.7).</summary>
public enum TrustedKeyKind
{
    /// <summary>An SSH server's host key.</summary>
    SshHostKey,

    /// <summary>A TLS server's certificate (docs/architecture.md §7.16).</summary>
    TlsCertificate,
}

/// <summary>
/// A server identity a user chose to trust (docs/architecture.md §7.7): one per host, kind, endpoint, port
/// and algorithm.
/// </summary>
public sealed class HostTrustedKey
{
    public const int AlgorithmMaxLength = 64;
    public const int FingerprintMaxLength = 128;

    public Guid Id { get; set; }

    public Guid HostId { get; set; }

    public TrustedKeyKind Kind { get; set; }

    /// <summary>The service's own address it was trusted for; empty for the host itself.</summary>
    public string Address { get; set; } = string.Empty;

    public int Port { get; set; }

    /// <summary>For an SSH host key, its algorithm, e.g. <c>ssh-ed25519</c>.</summary>
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>SHA-256, as unpadded base64 the way OpenSSH prints it (without <c>SHA256:</c>).</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public DateTimeOffset TrustedAtUtc { get; set; }

    public Guid? TrustedByUserId { get; set; }
}
