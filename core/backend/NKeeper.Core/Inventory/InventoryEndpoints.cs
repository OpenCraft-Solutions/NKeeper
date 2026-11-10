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

namespace NKeeper.Core.Inventory;

/// <summary>A credential typed into core's prompt (docs/architecture.md §7.6).</summary>
public sealed record CredentialPromptRequest(string? Username, string? Domain, string? Password, string? PrivateKey);

/// <param name="Reference">Single-use: the shell passes it to the plugin's endpoint, which exchanges it with the broker.</param>
public sealed record CredentialPromptResponse(string Reference, DateTimeOffset ExpiresAtUtc);

public static class InventoryEndpoints
{
    public const string VersionConflictCode = "version_conflict";
    public const string HostManageDeniedCode = "host_manage_denied";

    /// <summary>Hosts, tags, services and access (docs/architecture.md §7), with the service types the plugins contributed.</summary>
    public static IServiceCollection AddNKeeperInventory(this IServiceCollection services, IEnumerable<ServiceType> serviceTypes)
    {
        var types = serviceTypes.ToList();
        return services
            .AddSingleton(provider => new ServiceTypeCatalog(types, provider.GetRequiredService<ILogger<ServiceTypeCatalog>>()))
            .AddSingleton<CredentialProtector>()
            .AddSingleton<CredentialHandleStore>()
            .AddSingleton<CredentialPromptStore>()
            .AddScoped<ICredentialBroker, CredentialBroker>()
            .AddScoped<HostAccess>()
            .AddScoped<HostInventory>()
            .AddScoped<HostGrants>();
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

        hosts.MapPut("/{id:guid}", (Guid id, HostRequest? request, HostInventory inventory, HttpContext context) =>
            Handled(async () =>
                await inventory.UpdateAsync(id, request ?? new HostRequest(), RequestUser.From(context.User), context.RequestAborted) is { } host
                    ? Results.Ok(host)
                    : Results.NotFound()));

        hosts.MapDelete("/{id:guid}", (Guid id, HostInventory inventory, HttpContext context) =>
            Handled(async () =>
                await inventory.DeleteAsync(id, RequestUser.From(context.User), context.RequestAborted) ? Results.NoContent() : Results.NotFound()));

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

        var grants = api.MapGroup("/users/{id:guid}/grants").RequireAuthorization(policy => policy.RequireRole(SystemRoles.Administrator));

        grants.MapGet("/", async (Guid id, HostGrants hostGrants, HttpContext context) =>
            await hostGrants.GetAsync(id, context.RequestAborted) is { } list ? Results.Ok(list) : Results.NotFound());

        grants.MapPut("/", (Guid id, List<HostGrantRequest>? request, HostGrants hostGrants, HttpContext context) =>
            Handled(async () =>
                await hostGrants.ReplaceAsync(id, request ?? [], RequestUser.From(context.User).Id, context.RequestAborted) is { } list
                    ? Results.Ok(list)
                    : Results.NotFound()));
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
