namespace NKeeper.Abstractions;

/// <summary>
/// What a scan found (docs/architecture.md §7.26, SDK 1.4): a scan action's endpoint answers it. Nothing is saved;
/// the shell proposes changes from it and the user picks what to apply. Every field is optional: leave out what
/// couldn't be read, and say why in <see cref="Notes"/>.
/// </summary>
public sealed record ScanFindings
{
    public string? Hostname { get; init; }

    /// <summary>The address the host was reached on, or its main one.</summary>
    public string? IpAddress { get; init; }

    /// <summary>The main interface's MAC address.</summary>
    public string? MacAddress { get; init; }

    /// <summary>A name with its version, such as <c>Debian GNU/Linux 12 (bookworm)</c>; recorded as the <c>os</c> tag.</summary>
    public string? OperatingSystem { get; init; }

    /// <summary>Logical processors.</summary>
    public int? CpuCores { get; init; }

    public int? MemoryMiB { get; init; }

    /// <summary>Physical hardware's serial number, such as a Dell service tag.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>What the host runs under (<c>kvm</c>, <c>vmware</c>, <c>lxc</c>); null for physical hardware.</summary>
    public string? Virtualization { get; init; }

    /// <summary>Listening (or open) TCP ports.</summary>
    public IReadOnlyList<ScannedPort> Ports { get; init; } = [];

    /// <summary>The accounts that can log in.</summary>
    public IReadOnlyList<ScannedUser> Users { get; init; } = [];

    /// <summary>Machines in the host's neighbour (ARP/NDP) table, read without probing anything.</summary>
    public IReadOnlyList<ScannedNeighbor> Neighbors { get; init; } = [];

    /// <summary>How values were found, and what couldn't be read.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <param name="Name">A well-known name for it, such as <c>PostgreSQL</c>.</param>
/// <param name="ServiceType">The service type the scanner takes it to be, when it knows. Otherwise the shell matches the port to the type whose default port it is.</param>
public sealed record ScannedPort(int Port, string? Name = null, string? ServiceType = null);

public sealed record ScannedUser(string Username, string? FullName = null);

public sealed record ScannedNeighbor(string IpAddress, string? MacAddress = null);
