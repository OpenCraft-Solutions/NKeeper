using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Persistence;

namespace NKeeper.Server;

/// <summary>One audit event, with the host's name when its target is a host that exists.</summary>
public sealed record AuditEventResponse(
    long Id,
    DateTimeOffset OccurredAtUtc,
    string Actor,
    Guid? UserId,
    string? ClientAddress,
    string Action,
    string Target,
    string? HostName,
    string Detail);

/// <param name="Next">Pass as <c>before</c> for the next (older) page; null on the last.</param>
public sealed record AuditPageResponse(IReadOnlyList<AuditEventResponse> Events, long? Next);

/// <summary>The audit log page (docs/architecture.md §7.44), ported from 1.x's <c>AuditController</c>. Administrators only.</summary>
internal static class AuditEndpoints
{
    public const int MaxLimit = 500;
    public const int DefaultLimit = 100;
    public const int MaxCsvRows = 100_000;
    private const int MaxFilterLength = 256;
    private static readonly string[] TextFilters = ["q", "category", "actor", "target"];

    public static void MapAuditLog(this IEndpointRouteBuilder api)
    {
        var audit = api.MapGroup("/audit").RequireAuthorization(policy => policy.RequireRole(SystemRoles.Administrator));

        audit.MapGet("/events", async (HttpRequest request, CoreDbContext db, CancellationToken ct) =>
        {
            if (Parse(request) is not { } filter)
            {
                return Results.Problem("A filter is too long, or a time or number isn't one.", statusCode: StatusCodes.Status400BadRequest);
            }

            var events = await Filter(db, filter).OrderByDescending(e => e.Id).Take(filter.Limit + 1).ToListAsync(ct);
            var page = events.Take(filter.Limit).ToList();
            var names = await HostNamesAsync(db, page, ct);
            return Results.Ok(new AuditPageResponse([.. page.Select(e => Response(e, names))], events.Count > filter.Limit ? page[^1].Id : null));
        });

        audit.MapGet("/events.csv", async (HttpRequest request, HttpResponse response, CoreDbContext db, CancellationToken ct) =>
        {
            if (Parse(request) is not { } filter)
            {
                return Results.Problem("A filter is too long, or a time or number isn't one.", statusCode: StatusCodes.Status400BadRequest);
            }

            var hosts = await db.Hosts.AsNoTracking().Select(h => new { h.Id, h.Name }).ToDictionaryAsync(h => h.Id.ToString(), h => h.Name, ct);
            var csv = new StringBuilder("Time (UTC),Actor,Client address,Action,Target,Host,Detail\r\n");
            await foreach (var e in Filter(db, filter).OrderByDescending(e => e.Id).Take(MaxCsvRows).AsAsyncEnumerable().WithCancellation(ct))
            {
                csv.AppendJoin(',', [
                    Quote(e.OccurredAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    Quote(e.Actor), Quote(e.ClientAddress), Quote(e.Action), Quote(e.Target), Quote(hosts.GetValueOrDefault(e.Target)), Quote(e.Detail)]);
                csv.Append("\r\n");
            }

            response.Headers.CacheControl = "no-store";
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"nkeeper-audit-{DateTime.UtcNow:yyyy-MM-dd}.csv");
        });

        // The actions' first parts in use, for the category filter: host, auth, plugin, zabbix…
        audit.MapGet("/categories", async (CoreDbContext db, CancellationToken ct) =>
        {
            var actions = await db.AuditEvents.AsNoTracking().Select(e => e.Action).Distinct().ToListAsync(ct);
            return Results.Ok(actions.Select(Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
        });
    }

    internal sealed record AuditFilter(string? Q, string? Category, string? Actor, string? Target, DateTimeOffset? From, DateTimeOffset? To, long? Before, int Limit);

    /// <summary>The query string's filters; null when one is malformed or too long.</summary>
    internal static AuditFilter? Parse(HttpRequest request)
    {
        string? Text(string name)
        {
            var value = request.Query[name].ToString().Trim();
            return value.Length == 0 ? null : value;
        }

        var texts = TextFilters.Select(Text).ToList();
        if (texts.Any(t => t is { Length: > MaxFilterLength }))
        {
            return null;
        }

        DateTimeOffset? from = null, to = null;
        if ((Text("from") is { } f && !TryTime(f, out from)) || (Text("to") is { } t && !TryTime(t, out to)))
        {
            return null;
        }

        long? before = null;
        if (Text("before") is { } b)
        {
            if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return null;
            }

            before = id;
        }

        var limit = DefaultLimit;
        if (Text("limit") is { } l && !(int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit is >= 1 and <= MaxLimit))
        {
            return null;
        }

        return new AuditFilter(texts[0], texts[1], texts[2], texts[3], from, to, before, limit);
    }

    private static bool TryTime(string text, out DateTimeOffset? time)
    {
        var ok = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed);
        time = ok ? parsed : null;
        return ok;
    }

    private static IQueryable<AuditEvent> Filter(CoreDbContext db, AuditFilter filter)
    {
        var events = db.AuditEvents.AsNoTracking();
        if (filter.Before is { } before)
        {
            events = events.Where(e => e.Id < before);
        }

        if (filter.From is { } from)
        {
            events = events.Where(e => e.OccurredAtUtc >= from);
        }

        if (filter.To is { } to)
        {
            events = events.Where(e => e.OccurredAtUtc < to);
        }

        if (filter.Actor is { } actor)
        {
            events = events.Where(e => e.Actor == actor);
        }

        if (filter.Target is { } target)
        {
            events = events.Where(e => e.Target == target);
        }

        if (filter.Category is { } category)
        {
            var prefix = category + ".";
            events = events.Where(e => e.Action == category || e.Action.StartsWith(prefix));
        }

        if (filter.Q is { } q)
        {
            var pattern = $"%{q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            events = events.Where(e =>
                EF.Functions.ILike(e.Actor, pattern, "\\") || EF.Functions.ILike(e.Action, pattern, "\\")
                || EF.Functions.ILike(e.Target, pattern, "\\") || EF.Functions.ILike(e.Detail, pattern, "\\"));
        }

        return events;
    }

    /// <summary>The names of the hosts the page's targets are, while they exist.</summary>
    private static async Task<Dictionary<string, string>> HostNamesAsync(CoreDbContext db, IReadOnlyList<AuditEvent> events, CancellationToken ct)
    {
        var ids = events.Select(e => Guid.TryParse(e.Target, out var id) ? id : (Guid?)null).OfType<Guid>().Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Hosts.AsNoTracking().Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id.ToString(), h => h.Name, ct);
    }

    private static AuditEventResponse Response(AuditEvent e, Dictionary<string, string> hosts) =>
        new(e.Id, e.OccurredAtUtc, e.Actor, e.UserId, e.ClientAddress, e.Action, e.Target, hosts.GetValueOrDefault(e.Target), e.Detail);

    /// <summary>An action's first part: <c>host.update</c> is <c>host</c>, <c>network-scan.host</c> is <c>network-scan</c>.</summary>
    internal static string Category(string action) => action.Split('.', 2)[0];

    /// <summary>A CSV field, quoted, with a leading formula character neutralized so a spreadsheet doesn't run it.</summary>
    internal static string Quote(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
