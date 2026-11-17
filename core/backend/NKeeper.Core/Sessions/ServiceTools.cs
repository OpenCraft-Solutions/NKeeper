using Microsoft.EntityFrameworkCore;
using NKeeper.Abstractions;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Sessions;

/// <summary>The tools a service turns off (docs/architecture.md §7.4): session types core won't open on it.</summary>
internal static class ServiceTools
{
    public static Task<bool> IsDisabledAsync(CoreDbContext db, Guid serviceId, string sessionType, CancellationToken cancellationToken) =>
        db.HostServices.AnyAsync(s => s.Id == serviceId && s.DisabledTools.Contains(sessionType), cancellationToken);

    public static string DisabledMessage(SessionType sessionType, HostContext host, Abstractions.HostService service) =>
        $"{sessionType.Label} is turned off for {service.Label ?? service.Type} on “{host.Name}”. Someone who can manage the host can turn it on in the host's services.";
}
