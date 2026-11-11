using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace NKeeper.Server;

/// <summary>
/// The client's address and scheme from nginx (docs/architecture.md §8): <c>X-Forwarded-For</c> and
/// <c>X-Forwarded-Proto</c>, from a proxy on the loopback address only, for one hop. Without them the server
/// sees nginx's plain-HTTP request from 127.0.0.1, so the WebSocket origin check, secure cookies, the sign-in
/// rate limit and the audit's client addresses would all be wrong.
/// </summary>
internal static class ForwardedHeadersSetup
{
    public const string EnabledSetting = "ForwardedHeaders:Enabled";

    public static ForwardedHeadersOptions Options()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };

        // Only the local proxy: never a network, and nothing the defaults might add.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        return options;
    }

    /// <summary>First in the pipeline, when enabled, so everything after sees the browser's request.</summary>
    public static void UseNKeeperForwardedHeaders(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>(EnabledSetting))
        {
            app.UseForwardedHeaders(Options());
        }
    }
}
