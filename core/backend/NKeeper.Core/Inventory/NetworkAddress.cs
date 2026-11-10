using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NKeeper.Core.Inventory;

/// <summary>Validation and normalization of host addresses. Ported from 1.x.</summary>
public static partial class NetworkAddress
{
    /// <summary>Accepts dotted-quad IPv4 or IPv6, and gives the canonical text form.</summary>
    public static bool TryNormalizeIp(string value, out string normalized)
    {
        normalized = string.Empty;
        var candidate = value.Trim();

        // IPAddress.TryParse alone accepts shorthand such as "10" or "10.1", which is never meant here.
        var isIpv6 = candidate.Contains(':', StringComparison.Ordinal);
        if (!isIpv6 && !Ipv4Pattern().IsMatch(candidate))
        {
            return false;
        }

        if (!IPAddress.TryParse(candidate, out var address)
            || address.AddressFamily != (isIpv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork))
        {
            return false;
        }

        normalized = address.ToString();
        return true;
    }

    /// <summary>An RFC 1123 host name; underscores are tolerated, because Windows networks use them.</summary>
    public static bool IsValidHostname(string value) => HostnamePattern().IsMatch(value.Trim());

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"^(?=.{1,253}\.?$)[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?(?:\.[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?)*\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostnamePattern();
}
