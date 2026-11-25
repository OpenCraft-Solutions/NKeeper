using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Core.Transfer;

namespace NKeeper.Server;

/// <param name="LockAfterExport">1.x's lock: no changes to hosts and users here afterwards, until unlocked.</param>
public sealed record TransferExportRequest(string? Password, string? Passphrase, string? PassphraseConfirmation, bool LockAfterExport = false);

/// <summary>Whether the instance is locked after an export (§7.45), since when and by whom.</summary>
public sealed record TransferStatusResponse(bool Locked, DateTimeOffset? LockedAtUtc, string? LockedBy);

/// <summary>Moving an instance to a new server (docs/architecture.md §7.45), ported from 1.x's <c>InstanceTransferController</c>. Administrators only.</summary>
internal static class TransferEndpoints
{
    /// <summary>The largest file an import reads; nginx lets this through on the import's paths.</summary>
    public const long MaxBundleBytes = 100L * 1024 * 1024;

    public static void MapTransfer(this IEndpointRouteBuilder api)
    {
        var transfer = api.MapGroup("/transfer").RequireAuthorization(policy => policy.RequireRole(SystemRoles.Administrator));

        transfer.MapGet("/status", async (CoreDbContext db, CancellationToken ct) =>
            await db.InstanceLock.AsNoTracking().FirstOrDefaultAsync(ct) is { } row
                ? new TransferStatusResponse(true, row.LockedAtUtc, row.LockedBy)
                : new TransferStatusResponse(false, null, null));

        transfer.MapPost("/unlock", async (CoreDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            if (await db.InstanceLock.ExecuteDeleteAsync(ct) > 0)
            {
                await audit.RecordAsync(new AuditEntry(AuditActions.TransferUnlock, string.Empty, string.Empty), ct);
            }

            return Results.NoContent();
        });

        transfer.MapPost("/export", async (TransferExportRequest request, ClaimsPrincipal principal, UserManager<ApplicationUser> users, InstanceTransfer instance, CoreDbContext db, IAuditLog audit, HttpResponse response, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || string.IsNullOrEmpty(request.Password) || !await users.CheckPasswordAsync(user, request.Password))
            {
                return Results.Problem("Confirm the export with your own password.", statusCode: StatusCodes.Status403Forbidden);
            }

            if (request.Passphrase is not { Length: >= BundleFormat.MinimumPassphraseLength } passphrase)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["passphrase"] = [$"The passphrase needs at least {BundleFormat.MinimumPassphraseLength} characters."] });
            }

            if (request.PassphraseConfirmation != passphrase)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["passphraseConfirmation"] = ["The passphrases don't match."] });
            }

            var plaintext = await instance.ExportAsync(ct);
            try
            {
                var header = new BundleHeader(0, Version(), instance.LatestMigration(), DateTimeOffset.UtcNow, user.UserName!, string.Empty, 0, string.Empty, string.Empty, 0);
                using var bundle = new MemoryStream();
                await BundleFormat.WriteAsync(bundle, header, passphrase, plaintext, ct);
                await audit.RecordAsync(new AuditEntry(AuditActions.TransferExport, string.Empty, $"{bundle.Length} bytes"), ct);
                if (request.LockAfterExport && !await db.InstanceLock.AnyAsync(ct))
                {
                    db.InstanceLock.Add(new InstanceLockRow { LockedAtUtc = DateTimeOffset.UtcNow, LockedByUserId = user.Id, LockedBy = user.UserName! });
                    await db.SaveChangesAsync(ct);
                    await audit.RecordAsync(new AuditEntry(AuditActions.TransferLock, string.Empty, "after the export"), ct);
                }

                response.Headers.CacheControl = "no-store";
                return Results.File(bundle.ToArray(), "application/octet-stream", $"nkeeper-export-{DateTime.UtcNow:yyyy-MM-dd}.nkeeper");
            }
            finally
            {
                // The plaintext holds every secret: gone as soon as it's encrypted.
                Array.Clear(plaintext);
            }
        });

        transfer.MapPost("/import/preview", async (HttpRequest http, InstanceTransfer instance, CancellationToken ct) =>
        {
            var (bundle, problem) = await ReadAsync(http, ct);
            if (problem is not null)
            {
                return problem;
            }

            try
            {
                var (header, plaintext) = BundleFormat.Read(bundle!.Value.File, bundle.Value.Passphrase);
                try
                {
                    return Results.Ok((await instance.PreviewAsync(header, plaintext, ct)).Preview);
                }
                finally
                {
                    Array.Clear(plaintext);
                }
            }
            catch (BundleException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxBundleBytes + (64 * 1024)));

        transfer.MapPost("/import", async (HttpRequest http, ClaimsPrincipal principal, InstanceTransfer instance, IAuditLog audit, CancellationToken ct) =>
        {
            var (bundle, problem) = await ReadAsync(http, ct);
            if (problem is not null)
            {
                return problem;
            }

            if (!string.Equals(http.Form["confirm"], "true", StringComparison.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirm"] = ["Confirm that this instance's users and data are replaced by the file's."] });
            }

            var importer = principal.Identity?.Name ?? "administrator";
            try
            {
                var (header, plaintext) = BundleFormat.Read(bundle!.Value.File, bundle.Value.Passphrase);
                try
                {
                    var (data, preview) = await instance.PreviewAsync(header, plaintext, ct);
                    await instance.ImportAsync(header, data, Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty, ct);
                    // The new audit log's first event. The importer's account is gone: they're named, not linked.
                    await audit.RecordAsync(
                        new AuditEntry(AuditActions.TransferImport, string.Empty, $"{preview.Users} users, {preview.Hosts} hosts, from {header.ExportedBy}'s export of {header.ExportedAtUtc:u}", UserId: Guid.Empty, UserName: importer),
                        ct);
                    return Results.Ok(preview);
                }
                finally
                {
                    Array.Clear(plaintext);
                }
            }
            catch (BundleException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (ImportRefusedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxBundleBytes + (64 * 1024)));
    }

    /// <summary>The form's file and passphrase.</summary>
    private static async Task<((byte[] File, string Passphrase)? Bundle, IResult? Problem)> ReadAsync(HttpRequest http, CancellationToken ct)
    {
        if (!http.HasFormContentType)
        {
            return (null, Results.Problem("Send the export file as a form's file, \"file\", with its \"passphrase\".", statusCode: StatusCodes.Status400BadRequest));
        }

        var form = await http.ReadFormAsync(new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit = MaxBundleBytes }, ct);
        if (form.Files.GetFile("file") is not { } file || form["passphrase"].ToString() is not { Length: > 0 } passphrase)
        {
            return (null, Results.Problem("Send the export file as a form's file, \"file\", with its \"passphrase\".", statusCode: StatusCodes.Status400BadRequest));
        }

        if (file.Length > MaxBundleBytes)
        {
            return (null, Results.Problem($"An export file is at most {MaxBundleBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge));
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return ((buffer.ToArray(), passphrase), null);
    }

    private static string Version() =>
        typeof(TransferEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "2";
}
