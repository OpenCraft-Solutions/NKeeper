using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NKeeper.Core.Audit;
using NKeeper.Core.Inventory;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Trust;

/// <summary>Where a server identity was presented: one service of a host, at an endpoint and port.</summary>
/// <param name="Address">The service's own address; empty for the host itself.</param>
public sealed record TrustEndpoint(Guid HostId, Guid ServiceId, TrustedKeyKind Kind, string Address, int Port);

/// <summary>What checking a presented key against the trust store found.</summary>
public enum TrustVerdict
{
    Trusted,

    /// <summary>No key is trusted for the algorithm yet: the user must decide.</summary>
    Unknown,

    /// <summary>A different key is trusted for the algorithm.</summary>
    Changed,
}

/// <summary>Why a trust decision was refused.</summary>
public enum TrustRefusal
{
    /// <summary>Core didn't present this key, for this host and service, to this auth session lately.</summary>
    NotPresented,

    /// <summary>A different key was trusted meanwhile.</summary>
    Conflict,
}

/// <summary>
/// The trust store (docs/architecture.md §7.7): server identities users chose to trust, checked on every
/// connection core opens. Ported from 1.x's TrustedKeyStore.
/// </summary>
public sealed class TrustStore(CoreDbContext db, TrustPresentationStore presentations, IAuditLog audit, TimeProvider time, ILogger<TrustStore> logger)
{
    /// <summary>The name of a kind of key in responses.</summary>
    public static string KindName(TrustedKeyKind kind) => kind switch
    {
        TrustedKeyKind.SshHostKey => "ssh-host-key",
        TrustedKeyKind.TlsCertificate => "tls-certificate",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The keys trusted for an endpoint, read once before connecting.</summary>
    public async Task<IReadOnlyList<HostTrustedKey>> KnownAsync(TrustEndpoint endpoint, CancellationToken cancellationToken) =>
        await db.HostTrustedKeys.AsNoTracking()
            .Where(k => k.HostId == endpoint.HostId && k.Kind == endpoint.Kind && k.Address == endpoint.Address && k.Port == endpoint.Port)
            .ToListAsync(cancellationToken);

    /// <summary>Checks a presented key against the keys trusted for its endpoint. Doesn't record anything.</summary>
    public static TrustVerdict Check(IReadOnlyList<HostTrustedKey> known, string algorithm, string fingerprint) =>
        known.FirstOrDefault(k => k.Algorithm == algorithm) switch
        {
            null => TrustVerdict.Unknown,
            { } key when string.Equals(key.Fingerprint, fingerprint, StringComparison.Ordinal) => TrustVerdict.Trusted,
            _ => TrustVerdict.Changed,
        };

    /// <summary>
    /// Remembers that an unknown key was presented to this auth session, so the user can trust it.
    /// Without an auth session there's no one to ask, so nothing is remembered.
    /// </summary>
    public void Presented(string? sessionKey, TrustEndpoint endpoint, string algorithm, string fingerprint)
    {
        if (sessionKey is not null)
        {
            presentations.Add(sessionKey, new KeyPresentation(endpoint, algorithm, fingerprint));
        }
    }

    /// <summary>Audits a presented key that differs from the trusted one. Core never replaces it by itself.</summary>
    public async Task MismatchAsync(TrustEndpoint endpoint, string algorithm, string knownFingerprint, string presentedFingerprint, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "The {Kind} {Algorithm} of host {HostId} at {Address}:{Port} changed: trusted {KnownFingerprint}, presented {PresentedFingerprint}",
            endpoint.Kind, algorithm, endpoint.HostId, HostInventory.Endpoint(endpoint.Address), endpoint.Port, knownFingerprint, presentedFingerprint);
        await audit.RecordAsync(
            new AuditEntry(
                AuditActions.TrustMismatch,
                endpoint.HostId.ToString(),
                $"{KindName(endpoint.Kind)} {algorithm} at {HostInventory.Endpoint(endpoint.Address)}:{endpoint.Port}: trusted SHA256:{knownFingerprint}, presented SHA256:{presentedFingerprint}"),
            cancellationToken);
    }

    /// <summary>The user trusts a key core presented to them (§7.7). Each presentation can be trusted once.</summary>
    /// <returns>Null when trusted (or already trusted); otherwise why it was refused.</returns>
    public async Task<TrustRefusal?> TrustAsync(string sessionKey, Guid hostId, Guid serviceId, string algorithm, string fingerprint, Guid userId, CancellationToken cancellationToken)
    {
        if (presentations.Take(sessionKey, hostId, serviceId, algorithm, fingerprint) is not { } presentation)
        {
            return TrustRefusal.NotPresented;
        }

        var endpoint = presentation.Endpoint;
        switch (Check(await KnownAsync(endpoint, cancellationToken), algorithm, fingerprint))
        {
            case TrustVerdict.Trusted:
                return null;
            case TrustVerdict.Changed:
                return TrustRefusal.Conflict;
        }

        db.HostTrustedKeys.Add(new HostTrustedKey
        {
            Id = Guid.NewGuid(),
            HostId = endpoint.HostId,
            Kind = endpoint.Kind,
            Address = endpoint.Address,
            Port = endpoint.Port,
            Algorithm = algorithm,
            Fingerprint = fingerprint,
            TrustedAtUtc = time.GetUtcNow(),
            TrustedByUserId = userId,
        });
        try
        {
            // Not cancellable: the user already decided, and a half-recorded decision helps no one.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            // Someone trusted a key for the same endpoint and algorithm at the same moment, or the host is gone.
            db.ChangeTracker.Clear();
            return Check(await KnownAsync(endpoint, CancellationToken.None), algorithm, fingerprint) == TrustVerdict.Trusted ? null : TrustRefusal.Conflict;
        }

        await audit.RecordAsync(
            new AuditEntry(
                AuditActions.TrustAdd,
                endpoint.HostId.ToString(),
                $"{KindName(endpoint.Kind)} {algorithm} SHA256:{fingerprint} at {HostInventory.Endpoint(endpoint.Address)}:{endpoint.Port}, service {endpoint.ServiceId}"),
            CancellationToken.None);
        return null;
    }
}

/// <summary>An unknown key core presented to an auth session.</summary>
public sealed record KeyPresentation(TrustEndpoint Endpoint, string Algorithm, string Fingerprint);

/// <summary>
/// The unknown keys core presented, per auth session, waiting for the user to decide (docs/architecture.md §7.7).
/// Held for 10 minutes, at most 20 per session, and dropped at sign-out.
/// </summary>
public sealed class TrustPresentationStore(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxPerSession = 20;

    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];

    /// <summary>For tests: how many presentations are waiting.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public void Add(string sessionKey, KeyPresentation presentation)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            _entries.RemoveAll(e => e.ExpiresAtUtc <= now || (e.SessionKey == sessionKey && e.Presentation == presentation));
            var mine = _entries.Where(e => e.SessionKey == sessionKey).ToList();
            foreach (var oldest in mine.Take(Math.Max(0, mine.Count - MaxPerSession + 1)))
            {
                _entries.Remove(oldest);
            }

            _entries.Add(new Entry(sessionKey, presentation, now + Lifetime));
        }
    }

    /// <returns>The matching presentation, taken out; null when there's none.</returns>
    public KeyPresentation? Take(string sessionKey, Guid hostId, Guid serviceId, string algorithm, string fingerprint)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            var index = _entries.FindIndex(e => e.SessionKey == sessionKey
                && e.ExpiresAtUtc > now
                && e.Presentation.Endpoint.HostId == hostId
                && e.Presentation.Endpoint.ServiceId == serviceId
                && string.Equals(e.Presentation.Algorithm, algorithm, StringComparison.Ordinal)
                && string.Equals(e.Presentation.Fingerprint, fingerprint, StringComparison.Ordinal));
            if (index < 0)
            {
                return null;
            }

            var entry = _entries[index];
            _entries.RemoveAt(index);
            return entry.Presentation;
        }
    }

    /// <summary>At sign-out.</summary>
    public void DropSession(string sessionKey)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.SessionKey == sessionKey);
        }
    }

    private sealed record Entry(string SessionKey, KeyPresentation Presentation, DateTimeOffset ExpiresAtUtc);
}
