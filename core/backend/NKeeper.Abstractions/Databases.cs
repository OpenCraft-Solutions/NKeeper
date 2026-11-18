using System.Data.Common;

namespace NKeeper.Abstractions;

/// <summary>Where a database connection dials (SDK 1.7): the service's own address and port, or a jump host tunnel's.</summary>
public sealed record DatabaseEndpoint(string Address, int Port);

/// <summary>
/// How to open a database connection with the plugin's own driver (docs/architecture.md §7.31, SDK 1.7). Core builds the
/// connection string with <see cref="Build"/>, adds the login under the keywords named here, and opens it.
/// </summary>
/// <param name="Factory">The driver's factory, such as <c>SqlClientFactory.Instance</c>.</param>
/// <param name="Build">The connection string for an endpoint, without any login: core adds it.</param>
/// <param name="UserKeyword">The driver's keyword for the user name, such as <c>User ID</c>.</param>
/// <param name="PasswordKeyword">The driver's keyword for the password, such as <c>Password</c>.</param>
public sealed record DatabaseConnectRequest(DbProviderFactory Factory, Func<DatabaseEndpoint, DbConnectionStringBuilder> Build, string UserKeyword, string PasswordKeyword)
{
    /// <summary>The driver's keyword for a domain, if it has one; otherwise a domain is prefixed to the user name.</summary>
    public string? DomainKeyword { get; init; }

    /// <summary>How long opening may take, the driver's own timeout aside.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// An open database login (SDK 1.7): its first connection, and more on request. Disposing it closes every connection it
/// opened, and its jump host tunnel.
/// </summary>
public interface IDatabaseLogin : IAsyncDisposable
{
    /// <summary>The first connection, open.</summary>
    DbConnection Connection { get; }

    /// <summary>Another connection with the same login, open, such as a tool's beside the query window.</summary>
    /// <exception cref="ConnectException">It couldn't be opened.</exception>
    Task<DbConnection> OpenAnotherAsync(CancellationToken cancellationToken = default);
}
