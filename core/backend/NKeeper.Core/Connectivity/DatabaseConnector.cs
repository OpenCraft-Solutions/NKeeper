using System.Data.Common;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NKeeper.Abstractions;
using NKeeper.Core.Access;
using NKeeper.Core.Audit;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;
using NKeeper.Core.Trust;

namespace NKeeper.Core.Connectivity;

/// <summary>
/// Database connections for plugins (docs/architecture.md §7.31, SDK 1.7), with the plugin's own driver: core adds the
/// login, opens the connection, and refuses it if the driver kept the password. Also checks server identities a
/// plugin read in its own protocol against the trust store.
/// </summary>
public sealed class DatabaseConnector(
    CredentialBroker broker,
    CoreDbContext db,
    IHttpContextAccessor http,
    IJumpTunnels tunnels,
    TrustStore trust,
    IAuthSessionAccessor sessions,
    IAuditLog audit)
{
    public async Task<IDatabaseLogin> OpenAsync(HostContext host, Guid serviceId, CredentialHandle handle, DatabaseConnectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = http.HttpContext;
        var pluginId = PluginCaller.Of(context);
        if (context is null || pluginId is null || !ReferenceEquals(context.Features.Get<HostContext>(), host))
        {
            throw new InvalidOperationException("Database connections can only be opened from a plugin's host-scoped endpoint, for its own host.");
        }

        var (_, _, entry) = await broker.UseAsync(host, serviceId, handle, cancellationToken);
        var material = entry.Material;
        if (material.Username is not { Length: > 0 } username || material.Password?.Reveal() is not { Length: > 0 } password)
        {
            throw new ConnectException(ConnectFailure.NoSecret, "Signing in to the database needs a user name and password, and this credential doesn't have both.");
        }

        var service = host.Services.FirstOrDefault(s => s.Id == serviceId);
        var exists = await db.HostServices.AsNoTracking().AnyAsync(s => s.Id == serviceId && s.HostId == host.Id, cancellationToken);
        if (service?.Address is null || !exists)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"“{host.Name}” has no such service any more. Reload the host and try again.");
        }

        if (service.Port is not { } port)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"This service of “{host.Name}” has no port. Edit the host to set one.");
        }

        IJumpTunnel? tunnel;
        try
        {
            tunnel = await tunnels.OpenAsync(host, service.Address, port, cancellationToken);
        }
        catch (ConnectException ex)
        {
            throw new ConnectException(ConnectFailure.JumpHostUnavailable, ex.Message);
        }

        var login = new DatabaseLogin(
            request,
            tunnel is null ? new DatabaseEndpoint(service.Address, port) : new DatabaseEndpoint("127.0.0.1", tunnel.LocalPort),
            username,
            material.Domain,
            password,
            $"{service.Address}:{port}",
            tunnel);
        try
        {
            await login.OpenFirstAsync(cancellationToken);
            var credential = material.Source == CredentialSource.Saved ? $"credential {material.CredentialId}" : "a typed credential";
            await audit.RecordAsync(new AuditEntry(AuditActions.CredentialUse, host.Id.ToString(), $"database to service {serviceId}, plugin {pluginId}, {credential}"), cancellationToken);
            return login;
        }
        catch
        {
            await login.DisposeAsync();
            throw;
        }
    }

    /// <summary>A server identity the plugin read, checked as for an HTTPS certificate (§7.16).</summary>
    public async Task CheckIdentityAsync(HostContext host, Guid serviceId, PresentedIdentity identity, bool publiclyValid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var context = http.HttpContext;
        if (context is null || PluginCaller.Of(context) is null || !ReferenceEquals(context.Features.Get<HostContext>(), host))
        {
            throw new InvalidOperationException("Identities can only be checked from a plugin's host-scoped endpoint, for its own host.");
        }

        if (identity.Kind != PresentedIdentityKind.TlsCertificate || identity.Algorithm != PresentedIdentity.CertificateAlgorithm || identity.HostId != host.Id || identity.ServiceId != serviceId)
        {
            throw new ArgumentException("Only a TLS certificate of this host's service can be checked.", nameof(identity));
        }

        if (publiclyValid)
        {
            return;
        }

        var service = host.Services.FirstOrDefault(s => s.Id == serviceId);
        var stored = await db.HostServices.AsNoTracking().Where(s => s.Id == serviceId && s.HostId == host.Id).Select(s => new { s.Address }).FirstOrDefaultAsync(cancellationToken);
        if (service?.Port is not { } port || stored is null)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"“{host.Name}” has no such service any more. Reload the host and try again.");
        }

        var endpoint = new TrustEndpoint(host.Id, serviceId, TrustedKeyKind.TlsCertificate, stored.Address ?? string.Empty, port);
        var known = await trust.KnownAsync(endpoint, cancellationToken);
        switch (TrustStore.Check(known, PresentedIdentity.CertificateAlgorithm, identity.Fingerprint))
        {
            case TrustVerdict.Unknown:
                trust.Presented(sessions.CurrentSessionKey(context), endpoint, PresentedIdentity.CertificateAlgorithm, identity.Fingerprint);
                throw new ConnectException(ConnectFailure.HostKeyUnknown, $"“{host.Name}” presented a certificate that isn't publicly valid, and no one has trusted it yet.", identity);
            case TrustVerdict.Changed:
                var trusted = known.First(k => k.Algorithm == PresentedIdentity.CertificateAlgorithm).Fingerprint;
                await trust.MismatchAsync(endpoint, PresentedIdentity.CertificateAlgorithm, trusted, identity.Fingerprint, CancellationToken.None);
                throw new ConnectException(ConnectFailure.HostKeyChanged, $"“{host.Name}” presented a different certificate than the one trusted for it. Someone who manages the host must remove the old one first.");
        }
    }

    /// <summary>One login's connections. The password stays here, in core, for the login's life.</summary>
    private sealed class DatabaseLogin(
        DatabaseConnectRequest request, DatabaseEndpoint endpoint, string username, string? domain, string password, string display, IJumpTunnel? tunnel) : IDatabaseLogin
    {
        private readonly List<DbConnection> _opened = [];
        private readonly Lock _lock = new();
        private DbConnection? _first;

        public DbConnection Connection => _first ?? throw new InvalidOperationException("The login isn't open.");

        public async Task OpenFirstAsync(CancellationToken cancellationToken) => _first = await OpenAnotherAsync(cancellationToken);

        public async Task<DbConnection> OpenAnotherAsync(CancellationToken cancellationToken = default)
        {
            var builder = request.Build(endpoint);
            var domainKeyword = request.DomainKeyword;
            builder[request.UserKeyword] = domain is { Length: > 0 } && domainKeyword is null ? $"{domain}\\{username}" : username;
            builder[request.PasswordKeyword] = password;
            if (domain is { Length: > 0 } && domainKeyword is not null)
            {
                builder[domainKeyword] = domain;
            }

            try
            {
                builder["Persist Security Info"] = false;
            }
            catch (ArgumentException)
            {
                // A driver without the keyword; the check after opening still applies.
            }

            var connection = request.Factory.CreateConnection() ?? throw new InvalidOperationException("The driver's factory made no connection.");
            try
            {
                connection.ConnectionString = builder.ConnectionString;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(request.ConnectTimeout);
                try
                {
                    await connection.OpenAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ConnectException(ConnectFailure.TimedOut, $"{display} didn't answer in time.");
                }
                catch (DbException ex)
                {
                    throw new ConnectException(
                        ex.InnerException is SocketException || ex.GetBaseException() is SocketException ? ConnectFailure.Unreachable : ConnectFailure.AuthenticationFailed,
                        $"{display}: {Scrub(Messages(ex))}");
                }
                catch (SocketException)
                {
                    throw new ConnectException(ConnectFailure.Unreachable, $"Couldn't connect to {display}.");
                }

                // The driver must have forgotten the password (plugin-security.md §12.3): read with its own parser, so a user
                // name that happens to equal the password isn't mistaken for it.
                if (KeptPassword(connection.ConnectionString))
                {
                    throw new ConnectException(ConnectFailure.SecurityNegotiationFailed, "The database driver kept the password in its connection string after opening, so core didn't hand the connection over.");
                }

                lock (_lock)
                {
                    _opened.Add(connection);
                }

                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        /// <summary>Whether the connection string still holds the password, under the password keyword or any other.</summary>
        private bool KeptPassword(string? connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                return false;
            }

            var parsed = request.Factory.CreateConnectionStringBuilder() ?? new DbConnectionStringBuilder();
            try
            {
                parsed.ConnectionString = connectionString;
            }
            catch (ArgumentException)
            {
                // Unreadable: refuse it if the password appears at all.
                return connectionString.Contains(password, StringComparison.Ordinal);
            }

            return parsed.Keys.Cast<string>().Any(key =>
                !string.Equals(key, request.UserKeyword, StringComparison.OrdinalIgnoreCase)
                && parsed.TryGetValue(key, out var value) && string.Equals(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), password, StringComparison.Ordinal));
        }

        /// <summary>
        /// The driver's message with its inner exceptions' (SDK 1.9): some drivers wrap the server's own error, as ODP.NET
        /// 23 wraps the listener's ORA-12514 in ORA-50201, and an engine tells them apart by it.
        /// </summary>
        private static string Messages(Exception ex)
        {
            var messages = new List<string>();
            for (var e = ex; e is not null && messages.Count < 5; e = e.InnerException)
            {
                var message = e.Message.Trim();
                if (message.Length > 0 && !messages.Any(m => m.Contains(message, StringComparison.Ordinal)))
                {
                    messages.Add(message);
                }
            }

            return string.Join(" ", messages);
        }

        /// <summary>A driver's message, without the password should it repeat it.</summary>
        private string Scrub(string message) => message.Replace(password, "***", StringComparison.Ordinal);

        public async ValueTask DisposeAsync()
        {
            DbConnection[] opened;
            lock (_lock)
            {
                opened = [.. _opened];
                _opened.Clear();
            }

            foreach (var connection in opened)
            {
                await connection.DisposeAsync();
            }

            tunnel?.Dispose();
        }
    }
}
