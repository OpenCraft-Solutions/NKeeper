using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using NKeeper.Abstractions;

namespace NKeeper.Core.Credentials;

/// <summary>A resolved credential, secrets included. Kept by core, in memory only; disposing it clears the secrets.</summary>
public sealed class CredentialMaterial(CredentialSource source, Guid? credentialId, string? username, string? domain, Secret? password, Secret? privateKey) : IDisposable
{
    public CredentialSource Source { get; } = source;

    public Guid? CredentialId { get; } = credentialId;

    public string? Username { get; } = username;

    public string? Domain { get; } = domain;

    /// <summary>Also the private key's passphrase when both are set.</summary>
    public Secret? Password { get; } = password;

    public Secret? PrivateKey { get; } = privateKey;

    public CredentialDescription Describe() => new(Source, CredentialId, Username, Domain, Password is not null, PrivateKey is not null);

    public void Dispose()
    {
        Password?.Dispose();
        PrivateKey?.Dispose();
    }
}

/// <summary>What a handle refers to, and everything it's bound to (docs/architecture.md §7.6).</summary>
public sealed record CredentialHandleEntry(Guid UserId, Guid HostId, Guid ServiceId, string PluginId, Guid RequestId, CredentialMaterial Material);

/// <summary>Opaque random references, as handles and prompt references are made.</summary>
internal static class Tokens
{
    public static string New() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    /// <summary>Identifies this request among others, so a handle can be bound to it.</summary>
    public static Guid RequestId(HttpContext context)
    {
        const string key = "NKeeper.Credentials.RequestId";
        if (context.Items[key] is not Guid id)
        {
            id = Guid.NewGuid();
            context.Items[key] = id;
        }

        return id;
    }
}

/// <summary>The live credential handles. Each is dropped, and its secrets cleared, when the request that obtained it ends.</summary>
public sealed class CredentialHandleStore
{
    private readonly ConcurrentDictionary<string, CredentialHandleEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>For tests: how many handles are live.</summary>
    public int Count => _entries.Count;

    public CredentialHandle Add(HttpContext context, CredentialHandleEntry entry)
    {
        var token = Tokens.New();
        _entries[token] = entry;
        context.Response.RegisterForDispose(new Release(this, token));
        return new CredentialHandle(token);
    }

    public CredentialHandleEntry? Find(CredentialHandle handle) => _entries.GetValueOrDefault(handle.Token);

    private void Remove(string token)
    {
        if (_entries.TryRemove(token, out var entry))
        {
            entry.Material.Dispose();
        }
    }

    private sealed class Release(CredentialHandleStore store, string token) : IDisposable
    {
        public void Dispose() => store.Remove(token);
    }
}

/// <summary>
/// Credentials typed into core's prompt, waiting to be exchanged for a handle (docs/architecture.md §7.6).
/// Single-use, bound to the auth session, host and service, and dropped after 2 minutes or at sign-out.
/// </summary>
public sealed class CredentialPromptStore(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    public const int MaxPerSession = 10;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>For tests: how many prompt references are waiting.</summary>
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

    /// <returns>The prompt reference, and when it expires.</returns>
    public (string Reference, DateTimeOffset ExpiresAtUtc) Add(string sessionKey, Guid hostId, Guid serviceId, CredentialMaterial material)
    {
        var now = time.GetUtcNow();
        var reference = Tokens.New();
        lock (_lock)
        {
            Sweep(now);
            var mine = _entries.Where(e => e.Value.SessionKey == sessionKey).OrderBy(e => e.Value.CreatedAtUtc).ToList();
            foreach (var (oldest, _) in mine.Take(Math.Max(0, mine.Count - MaxPerSession + 1)))
            {
                Drop(oldest);
            }

            _entries[reference] = new Entry(sessionKey, hostId, serviceId, now, now + Lifetime, material);
        }

        return (reference, now + Lifetime);
    }

    /// <summary>
    /// Takes a prompt reference out, used up whatever happens next.
    /// </summary>
    /// <returns>Its credential, or null when it's unknown, expired, or for another session, host or service.</returns>
    public CredentialMaterial? Take(string reference, string? sessionKey, Guid hostId, Guid serviceId)
    {
        Entry? entry;
        lock (_lock)
        {
            if (!_entries.Remove(reference, out entry))
            {
                return null;
            }
        }

        if (entry.ExpiresAtUtc <= time.GetUtcNow() || entry.SessionKey != sessionKey || entry.HostId != hostId || entry.ServiceId != serviceId)
        {
            entry.Material.Dispose();
            return null;
        }

        return entry.Material;
    }

    /// <summary>At sign-out: drops the session's unused prompt references.</summary>
    public void DropSession(string sessionKey)
    {
        lock (_lock)
        {
            foreach (var reference in _entries.Where(e => e.Value.SessionKey == sessionKey).Select(e => e.Key).ToList())
            {
                Drop(reference);
            }
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var reference in _entries.Where(e => e.Value.ExpiresAtUtc <= now).Select(e => e.Key).ToList())
        {
            Drop(reference);
        }
    }

    private void Drop(string reference)
    {
        if (_entries.Remove(reference, out var entry))
        {
            entry.Material.Dispose();
        }
    }

    private sealed record Entry(string SessionKey, Guid HostId, Guid ServiceId, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, CredentialMaterial Material);
}
