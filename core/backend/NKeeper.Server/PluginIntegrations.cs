using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Plugins.Host;

namespace NKeeper.Server;

/// <summary>A loaded plugin's integration and what it's bound to, for the Settings page (docs/architecture.md §7.39).</summary>
public sealed record IntegrationResponse(
    string PluginId,
    string Name,
    string Label,
    IReadOnlyList<string> ServiceTypes,
    IntegrationBindingResponse? Binding);

/// <summary>An integration's binding: the host, service and credential, by ID and by what users see.</summary>
public sealed record IntegrationBindingResponse(
    Guid HostId,
    string HostName,
    Guid ServiceId,
    string ServiceLabel,
    Guid CredentialId,
    string CredentialLabel,
    DateTimeOffset SetAtUtc);

public sealed record IntegrationBindingRequest(Guid HostId, Guid ServiceId, Guid CredentialId);

/// <summary>Administrators bind and clear plugins' integrations (docs/architecture.md §7.39, SDK 1.13).</summary>
internal static class PluginIntegrationEndpoints
{
    public static void MapPluginIntegrations(this IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/plugins").RequireAuthorization(policy => policy.RequireRole(SystemRoles.Administrator));

        admin.MapGet("/integrations", async (PluginHostResult plugins, CoreDbContext db, CancellationToken ct) =>
        {
            var bindings = await db.PluginIntegrations.AsNoTracking().ToListAsync(ct);
            var hostIds = bindings.Select(b => b.HostId).ToList();
            var hosts = await db.Hosts.AsNoTracking().Include(h => h.Services).Include(h => h.Credentials)
                .Where(h => hostIds.Contains(h.Id)).AsSplitQuery().ToListAsync(ct);
            return plugins.Integrations.Select(i =>
            {
                var binding = bindings.FirstOrDefault(b => b.PluginId == i.PluginId && b.Name == i.Integration.Name);
                var host = binding is null ? null : hosts.FirstOrDefault(h => h.Id == binding.HostId);
                var service = host?.Services.FirstOrDefault(s => s.Id == binding!.ServiceId);
                var credential = host?.Credentials.FirstOrDefault(c => c.Id == binding!.CredentialId);
                return new IntegrationResponse(
                    i.PluginId,
                    i.Integration.Name,
                    i.Integration.Label,
                    i.Integration.ServiceTypes,
                    host is null || service is null || credential is null
                        ? null
                        : new IntegrationBindingResponse(
                            host.Id,
                            host.Name,
                            service.Id,
                            service.Label ?? service.Type,
                            credential.Id,
                            credential.Label ?? credential.Username ?? "credential",
                            binding!.SetAtUtc));
            }).ToList();
        });

        admin.MapPut("/{pluginId}/integrations/{name}", async (string pluginId, string name, IntegrationBindingRequest request, PluginHostResult plugins, CoreDbContext db, IAuditLog audit, ClaimsPrincipal user, TimeProvider time, CancellationToken ct) =>
        {
            if (plugins.Integrations.FirstOrDefault(i => i.PluginId == pluginId && i.Integration.Name == name) is not { } declared)
            {
                return Results.Problem("No loaded plugin declares that integration.", statusCode: StatusCodes.Status404NotFound);
            }

            var host = await db.Hosts.AsNoTracking().Include(h => h.Services).Include(h => h.Credentials).AsSplitQuery()
                .FirstOrDefaultAsync(h => h.Id == request.HostId, ct);
            var service = host?.Services.FirstOrDefault(s => s.Id == request.ServiceId);
            if (host is null || service is null)
            {
                return Results.Problem("That host has no such service.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!declared.Integration.ServiceTypes.Contains(service.Type))
            {
                return Results.Problem($"{declared.Integration.Label} needs a service of type {string.Join(" or ", declared.Integration.ServiceTypes)}.", statusCode: StatusCodes.Status400BadRequest);
            }

            var credential = host.Credentials.FirstOrDefault(c => c.Id == request.CredentialId);
            if (credential is null || (credential.ServiceIds is { } scope && !scope.Contains(service.Id)))
            {
                return Results.Problem("That credential isn't on the host, or isn't used with that service.", statusCode: StatusCodes.Status400BadRequest);
            }

            var binding = await db.PluginIntegrations.FirstOrDefaultAsync(b => b.PluginId == pluginId && b.Name == name, ct);
            if (binding is null)
            {
                binding = new PluginIntegrationBinding { PluginId = pluginId, Name = name };
                db.PluginIntegrations.Add(binding);
            }

            binding.HostId = host.Id;
            binding.ServiceId = service.Id;
            binding.CredentialId = credential.Id;
            binding.SetAtUtc = time.GetUtcNow();
            binding.SetByUserId = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(new AuditEntry(AuditActions.IntegrationSet, $"{pluginId}/{name}", $"host {host.Id}, service {service.Id}, credential {credential.Id}"), ct);
            return Results.NoContent();
        });

        admin.MapDelete("/{pluginId}/integrations/{name}", async (string pluginId, string name, CoreDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            if (await db.PluginIntegrations.Where(b => b.PluginId == pluginId && b.Name == name).ExecuteDeleteAsync(ct) == 0)
            {
                return Results.NotFound();
            }

            await audit.RecordAsync(new AuditEntry(AuditActions.IntegrationCleared, $"{pluginId}/{name}"), ct);
            return Results.NoContent();
        });
    }
}
