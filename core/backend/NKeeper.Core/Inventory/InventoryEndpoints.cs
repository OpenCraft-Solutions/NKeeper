using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKeeper.Abstractions;
using NKeeper.Core.Access;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;
using NKeeper.Core.Sessions;
using NKeeper.Core.Trust;

namespace NKeeper.Core.Inventory;

/// <summary>A credential typed into core's prompt (docs/architecture.md §7.6).</summary>
public sealed record CredentialPromptRequest(string? Username, string? Domain, string? Password, string? PrivateKey);

/// <param name="Reference">Single-use: the shell passes it to the plugin's endpoint, which exchanges it with the broker.</param>
public sealed record CredentialPromptResponse(string Reference, DateTimeOffset ExpiresAtUtc);

/// <summary>A key the user trusts: one core presented to them (docs/architecture.md §7.7).</summary>
public sealed record TrustKeyRequest(string? Algorithm, string? Fingerprint);

public static class InventoryEndpoints
{
    public const string VersionConflictCode = "version_conflict";
    public const string HostManageDeniedCode = "host_manage_denied";
    public const string TrustNotPresentedCode = "trust_not_presented";
    public const string TrustConflictCode = "trust_conflict";

    /// <summary>Hosts, tags, services and access (docs/architecture.md §7), with the service types the plugins contributed.</summary>
    public static IServiceCollection AddNKeeperInventory(this IServiceCollection services, IEnumerable<ServiceType> serviceTypes)
    {
        var types = serviceTypes.ToList();
        return services
            .AddSingleton(provider => new ServiceTypeCatalog(types, provider.GetRequiredService<ILogger<ServiceTypeCatalog>>()))
            .AddSingleton<CredentialProtector>()
            .AddSingleton<CredentialHandleStore>()
            .AddSingleton<CredentialPromptStore>()
            .AddScoped<CredentialBroker>()
            .AddScoped<ICredentialBroker>(provider => provider.GetRequiredService<CredentialBroker>())
            .AddScoped<NKeeper.Core.Connectivity.SshConnectionPlanner>()
            .AddScoped<NKeeper.Core.Connectivity.HttpConnector>()
            .AddScoped<NKeeper.Core.Connectivity.DesktopConnector>()
            .AddScoped<NKeeper.Core.Connectivity.IJumpTunnels, NKeeper.Core.Connectivity.NoJumpTunnels>()
            .AddSingleton<TrustPresentationStore>()
            .AddScoped<TrustStore>()
            .AddScoped<HostAccess>()
            .AddScoped<HostInventory>()
            .AddScoped<HostGrants>()
            .AddScoped<IPluginAudit, NKeeper.Core.Audit.PluginAudit>();
    }

    /// <summary><c>/api/hosts</c>, and <c>/api/users/{id}/grants</c> for administrators.</summary>
    public static void MapInventoryEndpoints(this IEndpointRouteBuilder api)
    {
        var hosts = api.MapGroup("/hosts");

        hosts.MapGet("/", (HostInventory inventory, HttpContext context) =>
            inventory.ListAsync(RequestUser.From(context.User), context.RequestAborted));

        hosts.MapGet("/{id:guid}", async (Guid id, HostInventory inventory, HttpContext context) =>
            await inventory.GetAsync(id, RequestUser.From(context.User), context.RequestAborted) is { } host ? Results.Ok(host) : Results.NotFound());

        hosts.MapPost("/", (HostRequest? request, HostInventory inventory, HttpContext context) =>
                Handled(async () =>
                {
                    var host = await inventory.CreateAsync(request ?? new HostRequest(), RequestUser.From(context.User), context.RequestAborted);
                    return Results.Created($"/api/hosts/{host.Id}", host);
                }))
            .RequireAuthorization(policy => policy.RequireRole(SystemRoles.Administrator));

        // A host's tags, services, credentials and jump host all bear on who may keep a session open (§7.10).
        hosts.MapPut("/{id:guid}", (Guid id, HostRequest? request, HostInventory inventory, SessionRegistry sessions, HttpContext context) =>
            Handled(async () =>
            {
                var host = await inventory.UpdateAsync(id, request ?? new HostRequest(), RequestUser.From(context.User), context.RequestAborted);
                await sessions.RecheckAsync(_ => true, context.RequestAborted);
                return host is null ? Results.NotFound() : Results.Ok(host);
            }));

        hosts.MapDelete("/{id:guid}", (Guid id, HostInventory inventory, SessionRegistry sessions, HttpContext context) =>
            Handled(async () =>
            {
                var deleted = await inventory.DeleteAsync(id, RequestUser.From(context.User), context.RequestAborted);
                await sessions.RecheckAsync(_ => true, context.RequestAborted);
                return deleted ? Results.NoContent() : Results.NotFound();
            }));

        // The core credential prompt (§7.6): typed secrets come here, never to a plugin's endpoint.
        hosts.MapPost("/{hostId:guid}/services/{serviceId:guid}/credential-prompt", async (
                Guid hostId,
                Guid serviceId,
                CredentialPromptRequest? request,
                HostAccess access,
                CoreDbContext db,
                CredentialPromptStore prompts,
                IAuthSessionAccessor sessions,
                HttpContext context) =>
            {
                if (await access.GetPermissionAsync(hostId, RequestUser.From(context.User), context.RequestAborted) == Access.HostPermission.None
                    || !await db.HostServices.AnyAsync(s => s.Id == serviceId && s.HostId == hostId, context.RequestAborted))
                {
                    return Results.NotFound();
                }

                if (sessions.CurrentSessionKey(context) is not { } sessionKey)
                {
                    return Results.Unauthorized();
                }

                request ??= new CredentialPromptRequest(null, null, null, null);
                var problem = request.Username?.Length > HostCredential.UsernameMaxLength ? ("username", HostCredential.UsernameMaxLength)
                    : request.Domain?.Length > HostCredential.DomainMaxLength ? ("domain", HostCredential.DomainMaxLength)
                    : request.Password?.Length > HostCredential.PasswordMaxLength ? ("password", HostCredential.PasswordMaxLength)
                    : request.PrivateKey?.Length > HostCredential.PrivateKeyMaxLength ? ("privateKey", HostCredential.PrivateKeyMaxLength)
                    : ((string, int)?)null;
                if (problem is var (field, max))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [$"Use at most {max} characters."] });
                }

                var material = new CredentialMaterial(
                    Abstractions.CredentialSource.Prompt,
                    null,
                    string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim(),
                    string.IsNullOrWhiteSpace(request.Domain) ? null : request.Domain.Trim(),
                    Secret.From(request.Password),
                    Secret.From(string.IsNullOrWhiteSpace(request.PrivateKey) ? null : request.PrivateKey.Trim()));
                var (reference, expiresAtUtc) = prompts.Add(sessionKey, hostId, serviceId, material);
                return Results.Ok(new CredentialPromptResponse(reference, expiresAtUtc));
            });

        // The user's trust decision (§7.7) comes to core, and only for a key core presented to them.
        hosts.MapPost("/{hostId:guid}/services/{serviceId:guid}/trusted-keys", async (
                Guid hostId,
                Guid serviceId,
                TrustKeyRequest? request,
                HostAccess access,
                TrustStore trust,
                IAuthSessionAccessor sessions,
                HttpContext context) =>
            {
                var user = RequestUser.From(context.User);
                if (await access.GetPermissionAsync(hostId, user, context.RequestAborted) == Access.HostPermission.None)
                {
                    return Results.NotFound();
                }

                if (sessions.CurrentSessionKey(context) is not { } sessionKey)
                {
                    return Results.Unauthorized();
                }

                return await trust.TrustAsync(sessionKey, hostId, serviceId, request?.Algorithm ?? string.Empty, request?.Fingerprint ?? string.Empty, user.Id, context.RequestAborted) switch
                {
                    null => Results.NoContent(),
                    TrustRefusal.Conflict => Coded(
                        StatusCodes.Status409Conflict, TrustConflictCode, "Trusted meanwhile", "A different key was trusted for this server meanwhile. Reload the host and check its keys."),
                    _ => Coded(
                        StatusCodes.Status400BadRequest, TrustNotPresentedCode, "Not presented", "This key wasn't presented to you for this service lately. Connect again to see the server's key."),
                };
            });

        hosts.MapDelete("/{hostId:guid}/trusted-keys/{keyId:guid}", (Guid hostId, Guid keyId, HostInventory inventory, HttpContext context) =>
            Handled(async () =>
                await inventory.RemoveTrustedKeyAsync(hostId, keyId, RequestUser.From(context.User), context.RequestAborted) ? Results.NoContent() : Results.NotFound()));

        var grants = api.MapGroup("/users/{id:guid}/grants").RequireAuthorization(policy => policy.RequireRole(SystemRoles.Administrator));

        grants.MapGet("/", async (Guid id, HostGrants hostGrants, HttpContext context) =>
            await hostGrants.GetAsync(id, context.RequestAborted) is { } list ? Results.Ok(list) : Results.NotFound());

        grants.MapPut("/", (Guid id, List<HostGrantRequest>? request, HostGrants hostGrants, SessionRegistry sessions, HttpContext context) =>
            Handled(async () =>
            {
                var list = await hostGrants.ReplaceAsync(id, request ?? [], RequestUser.From(context.User).Id, context.RequestAborted);
                await sessions.RecheckAsync(s => s.UserId == id, context.RequestAborted);
                return list is null ? Results.NotFound() : Results.Ok(list);
            }));
    }

    /// <summary>Runs an operation, turning a broken rule, a denied change or a stale version into its problem response.</summary>
    private static async Task<IResult> Handled(Func<Task<IResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (InventoryValidationException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.Field] = [ex.Message] });
        }
        catch (HostManageDeniedException ex)
        {
            return Coded(StatusCodes.Status403Forbidden, HostManageDeniedCode, "Not allowed", ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Coded(StatusCodes.Status409Conflict, VersionConflictCode, "Changed meanwhile", "Someone else changed this host. Reload it and make your changes again.");
        }
    }

    private static IResult Coded(int status, string code, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
