using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NKeeper.Abstractions;
using NKeeper.Core.Access;
using NKeeper.Core.Inventory;
using NKeeper.Core.Persistence;
using Host = NKeeper.Core.Persistence.Host;

namespace NKeeper.Core.Health;

/// <summary>Service health checks (section <c>Health</c>, docs/architecture.md §7.13).</summary>
public sealed class HealthOptions
{
    public const string SectionName = "Health";

    /// <summary>Check every host's services in the background.</summary>
    public bool Enabled { get; set; } = true;

    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long one check may take before the service counts as down.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Checks run at the same time.</summary>
    [Range(1, 256)]
    public int Parallelism { get; set; } = 32;
}

public enum ServiceHealthState
{
    Up,
    Down,
}

/// <summary>Why a service is down.</summary>
public enum ServiceDownReason
{
    /// <summary>Something answered, and refused the connection.</summary>
    Refused,

    /// <summary>Nothing answered in time.</summary>
    Timeout,

    /// <summary>The address couldn't be resolved or reached.</summary>
    Unreachable,
}

/// <summary>The latest check of one service.</summary>
/// <param name="LatencyMs">How long the check took.</param>
/// <param name="Reason">Why it's down; null when it's up.</param>
/// <param name="CertificateSubject">For a TLS probe that completed its handshake: the certificate's subject.</param>
/// <param name="CertificateExpiresAtUtc">For a TLS probe that completed its handshake: when the certificate expires.</param>
public sealed record ServiceHealthResponse(
    Guid HostId,
    Guid ServiceId,
    ServiceHealthState State,
    int LatencyMs,
    DateTimeOffset CheckedAtUtc,
    ServiceDownReason? Reason,
    string? CertificateSubject = null,
    DateTimeOffset? CertificateExpiresAtUtc = null);

/// <summary>One probe of an address and port.</summary>
internal sealed record ProbeResult(int LatencyMs, DateTimeOffset At, ServiceDownReason? Reason, string? CertificateSubject = null, DateTimeOffset? CertificateExpiresAtUtc = null);

/// <summary>
/// The latest results, in memory only: after a restart, services have no status until their next check.
/// A result never replaces a newer one, so a round that ran long can't undo a check made meanwhile.
/// </summary>
public sealed class ServiceHealthResults
{
    private readonly Lock _lock = new();
    private Dictionary<Guid, ServiceHealthResponse> _results = [];

    public IReadOnlyList<ServiceHealthResponse> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _results.Values];
            }
        }
    }

    /// <summary>
    /// A full round's results, which began at <paramref name="startedAtUtc"/>. Services it didn't check drop
    /// out, unless they were checked since it began (a host added and checked meanwhile, say).
    /// </summary>
    public void ReplaceAll(IEnumerable<ServiceHealthResponse> results, DateTimeOffset startedAtUtc)
    {
        lock (_lock)
        {
            var next = results.ToDictionary(r => r.ServiceId);
            foreach (var current in _results.Values.Where(r => r.CheckedAtUtc >= startedAtUtc))
            {
                if (!next.TryGetValue(current.ServiceId, out var checkedInRound) || checkedInRound.CheckedAtUtc < current.CheckedAtUtc)
                {
                    next[current.ServiceId] = current;
                }
            }

            _results = next;
        }
    }

    /// <summary>Replaces one host's results with a check that began at <paramref name="startedAtUtc"/>.</summary>
    public void ReplaceHost(Guid hostId, IEnumerable<ServiceHealthResponse> results, DateTimeOffset startedAtUtc)
    {
        lock (_lock)
        {
            foreach (var stale in _results.Values.Where(r => r.HostId == hostId && r.CheckedAtUtc < startedAtUtc).ToList())
            {
                _results.Remove(stale.ServiceId);
            }

            foreach (var result in results)
            {
                if (!_results.TryGetValue(result.ServiceId, out var current) || current.CheckedAtUtc <= result.CheckedAtUtc)
                {
                    _results[result.ServiceId] = result;
                }
            }
        }
    }
}

/// <summary>
/// Runs the service types' health probes (docs/architecture.md §7.13). Core runs them, never a plugin. Only
/// TCP so far: a connection to the service's address and port, closed at once, with nothing sent and no
/// credential used. Hosts reached through a jump host aren't checked. Ported from 1.x's HostHealthChecker.
/// </summary>
public sealed class ServiceHealthChecker(
    IServiceScopeFactory scopes,
    ServiceTypeCatalog serviceTypes,
    ServiceHealthResults results,
    IOptions<HealthOptions> options,
    TimeProvider time) : IDisposable
{
    // Health:Parallelism caps every probe at once: the background round's and on-demand checks' together.
    private readonly SemaphoreSlim _probes = new(options.Value.Parallelism, options.Value.Parallelism);

    public void Dispose() => _probes.Dispose();

    /// <summary>Checks every host, and replaces the results; returns how many services were checked.</summary>
    public async Task<int> CheckAllAsync(CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        List<Host> hosts;
        await using (var scope = scopes.CreateAsyncScope())
        {
            hosts = await scope.ServiceProvider.GetRequiredService<CoreDbContext>().Hosts.AsNoTracking()
                .Where(h => h.JumpHostId == null)
                .Include(h => h.Services)
                .ToListAsync(cancellationToken);
        }

        var checks = new ConcurrentBag<ServiceHealthResponse>();
        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions { MaxDegreeOfParallelism = options.Value.Parallelism, CancellationToken = cancellationToken },
            async (host, token) =>
            {
                foreach (var check in await CheckAsync(host, token))
                {
                    checks.Add(check);
                }
            });
        results.ReplaceAll(checks, startedAt);
        return checks.Count;
    }

    /// <summary>Checks one host now, and replaces its results.</summary>
    public async Task<List<ServiceHealthResponse>> CheckHostAsync(Guid hostId, CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        Host? host;
        await using (var scope = scopes.CreateAsyncScope())
        {
            host = await scope.ServiceProvider.GetRequiredService<CoreDbContext>().Hosts.AsNoTracking()
                .Include(h => h.Services)
                .FirstOrDefaultAsync(h => h.Id == hostId, cancellationToken);
        }

        var checks = host is null || host.JumpHostId is not null ? [] : await CheckAsync(host, cancellationToken);
        results.ReplaceHost(hostId, checks, startedAt);
        return checks;
    }

    private async Task<List<ServiceHealthResponse>> CheckAsync(Host host, CancellationToken cancellationToken)
    {
        var targets = host.Services
            .Select(s => (Service: s, Type: serviceTypes.Find(s.Type)))
            .Where(t => t.Type?.Health is not null)
            .Select(t => (t.Service, Probe: t.Type!.Health!, Address: t.Service.Address ?? host.IpAddress ?? host.Hostname, Port: t.Service.Port ?? t.Type!.DefaultPort))
            .Where(t => t.Address is not null && t.Port is not null)
            .ToList();

        // The same address, port and probe once, even when two services share them.
        var probes = new Dictionary<(string, int, string), ProbeResult>();
        var checks = new List<ServiceHealthResponse>(targets.Count);
        foreach (var (service, kind, address, port) in targets)
        {
            var key = (address!.ToUpperInvariant(), port!.Value, kind.Kind);
            if (!probes.TryGetValue(key, out var probe))
            {
                probe = probes[key] = await ProbeAsync(kind, address, port.Value, cancellationToken);
            }

            checks.Add(new ServiceHealthResponse(
                host.Id,
                service.Id,
                probe.Reason is null ? ServiceHealthState.Up : ServiceHealthState.Down,
                probe.LatencyMs,
                probe.At,
                probe.Reason,
                probe.CertificateSubject,
                probe.CertificateExpiresAtUtc));
        }

        return checks;
    }

    private async Task<ProbeResult> ProbeAsync(HealthProbe kind, string address, int port, CancellationToken cancellationToken)
    {
        await _probes.WaitAsync(cancellationToken);
        try
        {
            var probe = await ProbeNowAsync(address, port, cancellationToken);
            if (probe.Reason is not null || kind != HealthProbe.Tls)
            {
                return probe;
            }

            // TLS: the certificate is a bonus. A server that answers but won't complete a handshake is still up.
            try
            {
                using var presented = await Connectivity.HttpConnector.HandshakeAsync(address, port, cancellationToken);
                return probe with { CertificateSubject = presented.Certificate.Subject, CertificateExpiresAtUtc = presented.Certificate.NotAfter.ToUniversalTime() };
            }
            catch (ConnectException)
            {
                return probe;
            }
        }
        finally
        {
            _probes.Release();
        }
    }

    private async Task<ProbeResult> ProbeNowAsync(string address, int port, CancellationToken cancellationToken)
    {
        var at = time.GetUtcNow();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.Timeout);
        var watch = Stopwatch.StartNew();
        ServiceDownReason? reason = null;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            reason = ServiceDownReason.Timeout;
        }
        catch (SocketException ex)
        {
            reason = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => ServiceDownReason.Refused,
                SocketError.TimedOut => ServiceDownReason.Timeout,
                _ => ServiceDownReason.Unreachable,
            };
        }

        return new ProbeResult((int)watch.ElapsedMilliseconds, at, reason);
    }
}

/// <summary>Checks every host's services every <see cref="HealthOptions.Interval"/>, starting soon after startup.</summary>
public sealed class ServiceHealthMonitor(ServiceHealthChecker checker, IOptions<HealthOptions> options, TimeProvider time, ILogger<ServiceHealthMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.Interval, time);
        do
        {
            try
            {
                var count = await checker.CheckAllAsync(stoppingToken);
                logger.LogDebug("Checked {Count} services", count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Checking services failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class ServiceHealthEndpoints
{
    public static IServiceCollection AddNKeeperHealth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HealthOptions>().Bind(configuration.GetSection(HealthOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        return services
            .AddSingleton<ServiceHealthResults>()
            .AddSingleton<ServiceHealthChecker>()
            .AddHostedService<ServiceHealthMonitor>();
    }

    /// <summary><c>GET /api/health</c> and <c>POST /api/hosts/{hostId}/health</c> (docs/architecture.md §7.13).</summary>
    public static void MapHealthEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/health", async (ServiceHealthResults results, HostAccess access, HttpContext context) =>
        {
            var visible = (await access.Visible(RequestUser.From(context.User)).Select(h => h.Id).ToListAsync(context.RequestAborted)).ToHashSet();
            return results.All.Where(r => visible.Contains(r.HostId)).OrderBy(r => r.HostId).ThenBy(r => r.ServiceId).ToList();
        });

        api.MapPost("/hosts/{hostId:guid}/health", async (Guid hostId, ServiceHealthChecker checker, HostAccess access, HttpContext context) =>
            await access.GetPermissionAsync(hostId, RequestUser.From(context.User), context.RequestAborted) == Access.HostPermission.None
                ? Results.NotFound()
                : Results.Ok(await checker.CheckHostAsync(hostId, context.RequestAborted)));
    }
}
