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

    /// <summary>
    /// For a hypervisor (SDK 1.15, docs/architecture.md §7.49): the virtual machines it runs, or a cluster's. The shell
    /// proposes linking each to the host it matches, as running here, or adding a host for it.
    /// </summary>
    public IReadOnlyList<ScannedGuest> Guests { get; init; } = [];

    /// <summary>How values were found, and what couldn't be read.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <param name="Name">A well-known name for it, such as <c>PostgreSQL</c>.</param>
/// <param name="ServiceType">The service type the scanner takes it to be, when it knows. Otherwise the shell matches the port to the type whose default port it is.</param>
public sealed record ScannedPort(int Port, string? Name = null, string? ServiceType = null);

public sealed record ScannedUser(string Username, string? FullName = null);

public sealed record ScannedNeighbor(string IpAddress, string? MacAddress = null);

/// <summary>A virtual machine (or container) a hypervisor runs, as the hypervisor reports it (SDK 1.15).</summary>
/// <param name="Name">The hypervisor's name for it.</param>
/// <param name="State">The hypervisor's word for its state, such as <c>running</c>.</param>
/// <param name="MacAddresses">Its network interfaces' MAC addresses, the surest way to tell which host it is.</param>
/// <param name="IpAddresses">Addresses the hypervisor knows it has (a guest agent's, or a lease).</param>
/// <param name="CpuCores">The vCPUs it's given.</param>
/// <param name="MemoryMiB">The memory it's given.</param>
/// <param name="Node">For a cluster: the name of the node it runs on; null for the scanned host itself.</param>
public sealed record ScannedGuest(
    string Name,
    string? State = null,
    IReadOnlyList<string>? MacAddresses = null,
    IReadOnlyList<string>? IpAddresses = null,
    int? CpuCores = null,
    int? MemoryMiB = null,
    string? Node = null);
