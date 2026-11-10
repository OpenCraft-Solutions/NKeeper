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
