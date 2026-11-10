# NKeeper 2.0 architecture

How NKeeper is put together today: the parts of core, how plugins plug in, and the rules that keep the two apart. The plan and its reasoning are in [v2-plan.md](v2-plan.md); how packages are signed, verified and loaded is in [plugin-security.md](plugin-security.md). This document describes what is built, and grows with each phase.

> **Core provides capabilities. Plugins consume capabilities and contribute functionality. Core never needs to know which plugins exist.**

## 1. The parts

| Part | Where | What it is |
| --- | --- | --- |
| **SDK (backend)** | `core/backend/NKeeper.Abstractions` | The contracts a plugin builds against: `IPlugin`, `PluginContext`, `SdkVersion`. Nothing else. |
| **SDK (frontend)** | `core/frontend/sdk` (`@nkeeper/sdk`) | `definePlugin`, the API client (`api`, `ApiError`, `onUnauthorized`, `apiWebSocketUrl`, `readJsonLines`). |
| **Packaging** | `core/backend/NKeeper.Plugins.Packaging` | The `.nkp` format, trust and signature verification: bytes in, a result out. No I/O of its own. |
| **Plugin host** | `core/backend/NKeeper.Plugins.Host` | Directory and trust checks, pinning, dependency resolution, one load context per plugin, enable state. |
| **Persistence** | `core/backend/NKeeper.Core.Persistence` | `CoreDbContext` (schema `core`), its migrations, and the stores the host writes plugin records and audit events to. |
| **Kernel** | `core/backend/NKeeper.Core` | Core services every feature uses. Today: the audit log, hosts and access (§7). |
| **Identity** | `core/backend/NKeeper.Identity` | Accounts, sign-in, server-side sessions, the key ring and the request policies (§6). |
| **Server** | `core/backend/NKeeper.Server` | The composition root: startup order, `GET /api/plugins`, plugin frontend files, and the shell with its security policy. |
| **Shared runtime** | `core/frontend/runtime` (`@nkeeper/runtime`) | Builds the modules the shell shares with plugins, and the import map that names them. |
| **Shell** | `core/frontend/app` | The single-page app. Loads each plugin's frontend module. |
| **Tooling** | `tools/NKeeper.PluginTool` (`nkeeper-plugin`) | `keygen`, `pack`, `verify`. |
| **Plugins** | `plugins/<id>/` | One folder per plugin: backend, frontend, tests. None yet; SSH arrives in Phase 1. |
| **Test plugin** | `testing/plugins/NKeeper.TestPlugins.Hello` | A no-op plugin, built and packaged like a real one, used by the tests. |

The rest of core in [v2-plan.md §6](v2-plan.md#6-proposed-repository-structure) (`NKeeper.Core`, `NKeeper.Identity`, `NKeeper.Connectivity.Ssh`, the UI kit) arrives from Phase 1.

## 2. Dependency rules

Each rule is enforced by a test in `core/backend/tests/NKeeper.Architecture.Tests`, so breaking one fails CI.

1. **A plugin's backend references only `NKeeper.Abstractions`** (and packages from NuGet). Never another core project. The reference doesn't copy the SDK into the plugin's output (`Private="false" ExcludeAssets="runtime"`), because the SDK is shared from the server ([plugin-security.md §7.1](plugin-security.md#71-backend)). The test checks both the project file and the built assembly's references. A plugin that depends on another plugin (`dependsOn`, [v2-plan.md §4.5](v2-plan.md#45-kinds-of-dependency)) will be allowed to reference that plugin's project too, once the first such plugin is ported.
2. **No core project references a plugin**, directly or through another project. Plugins reach the server only as signed packages. The test checks the project graph and the server's built dependency list (`NKeeper.Server.deps.json`).
3. **The SDK references no other NKeeper project**, so a plugin can never reach core internals through it.
4. **Core frontend packages depend on no plugin package**, and a plugin's frontend depends on no `@nkeeper/*` package except `@nkeeper/sdk`.
5. **Core never names a service type or plugin ID.** No string literal in core source (backend, frontend or tooling; tests excluded) equals one. The IDs checked are the folder names under `plugins/`, plus the service types the plan names, so the rule holds before those plugins exist. If core ever needs such a literal for a reason that isn't about the plugin (for example a trust-store scope), the test's allow-list says why.

## 3. Startup

`NKeeper.Server` starts in this order ([plugin-security.md §9](plugin-security.md#9-operations-and-visibility)). A problem with the server itself (its directories, its shell, its database) stops it; a problem with one plugin refuses that plugin, with a reason code, and the rest carry on.

1. Refuse to run as root, outside Development.
2. If it serves the shell (`Frontend:Root`), read `index.html` and check it: no inline script except at most one import map. Derive the Content Security Policy from the import map's hash.
3. Migrate the core schema, then bring the key ring in line with its certificate (§6.3).
4. Plugin host: check the plugin and trust directories, verify every package, check pins and dependencies, and load the enabled plugins from their verified bytes, each into its own `AssemblyLoadContext`.
5. Call each loaded plugin's `ConfigureServices`. If one throws, its registrations are rolled back, and it and the plugins that depend on it are refused (`load_failed`); the others carry on. With `Plugins:RequireAllEnabledLoaded`, any enabled plugin that didn't load stops startup instead.
6. Build the application and create the bootstrap administrator, if one is configured (§6.1). Then map core endpoints, each plugin's endpoints (`MapEndpoints`), and the shell's fallback route. A plugin that fails in `MapEndpoints` stops startup, because endpoints can't be unmapped.

Plugin changes take effect at the next restart. So does a rebuilt shell.

## 4. Backend extension point (SDK v0)

A plugin's manifest names an entry type that implements `IPlugin`, with a public parameterless constructor:

```csharp
public sealed class WakeOnLanPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, PluginContext context) =>
        services.AddSingleton<MagicPacketSender>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context) =>
        endpoints.MapPost("/send", (MagicPacketSender sender) => sender.SendAsync());
}
```

- `PluginContext` holds the plugin's ID and version, from the verified manifest rather than the plugin's own code, and its configuration section, `Plugins:Settings:<id>`.
- `endpoints` is a route group core created at `/api/p/<id>/`, inside core's `/api` group. Every policy in §6.4 applies to it, so a plugin can't forget one ([v2-plan.md §4.4](v2-plan.md#44-guardrails-built-into-the-sdk)).
- More contracts (`ServiceType`, `SessionType`, `HostAction`, `HealthProbe`, settings, audit actions) arrive with their first real users ([v2-plan.md §4.2](v2-plan.md#42-contribution-catalogue)). The SDK may change until it is frozen as v1 at the end of Phase 3. A manifest declares the SDK range it supports, and a package outside the server's `SdkVersion` is refused.

## 5. Frontend extension point

**Shared modules.** The shell and every plugin use one copy each of `react`, `react/jsx-runtime`, `react-dom`, `react-dom/client` and `@nkeeper/sdk`. `@nkeeper/runtime` builds each as an ES module with a content hash in its name, under `/runtime/`, plus an import map naming them. The shell's build puts the import map first in `index.html`'s `<head>`. A plugin's bundle marks those modules external and imports them by name.

**A plugin's entry module** is the file its manifest names as `frontend.entry`. It default-exports a definition:

```ts
import { definePlugin } from '@nkeeper/sdk'

export default definePlugin({
  id: 'wake-on-lan',
  activate(context) {
    // register contributions here as the SDK gains them
  },
})
```

**Loading.**
1. The shell asks `GET /api/plugins`. For each loaded plugin with a frontend, the answer includes its entry URL, `/plugins/<id>/<version>/<hash tag>/<path>`, and the entry file's SHA-256.
2. The shell imports only same-origin URLs under `/plugins/<that id>/`.
3. It checks that the module's default export is a plugin definition whose `id` matches the verified manifest, then calls `activate`.
4. A failure affects only that plugin, and the shell shows it.

The server serves plugin files from the verified in-memory copy of the package, not from disk, and only allow-listed file types under `frontend/`. The page's policy allows scripts only from the server itself, plus the import map by its hash ([plugin-security.md §7.2, §7.3](plugin-security.md#72-frontend)).

## 6. Identity and request policies

Ported from 1.x, with ASP.NET Core Identity. Users, roles and sessions are tables in the core schema.

### 6.1 Accounts and sign-in

- A user signs in with their user name or email address and a password of at least 12 characters.
- Five wrong passwords lock the account for 15 minutes.
- Every failure looks the same to the caller: an unknown account, a wrong password, a disabled or locked account all give `401` with `invalid_credentials`, after comparable time.
- The `Administrator` role is the only system role. Access to hosts comes from grants, in Phase 1 step 3.
- **Bootstrap administrator.** `BootstrapAdmin:Enabled` with a user name, email and password creates the first administrator at startup, once. An account that already exists is left alone. Outside Development it also needs `BootstrapAdmin:AllowOutsideDevelopment`, which the installer sets for the first start.
- **User administration** (`/api/users`, administrators only) lists, creates, edits, disables and deletes accounts, and resets passwords. As in 1.x, it can't lock everyone out:
  - administrators can't demote, disable or delete themselves;
  - one active administrator always remains.
- **A user's sessions end** when their access is cut (disabled, or no longer an administrator), when their role changes (roles are carried in the session), when an administrator resets their password, and when the account is deleted.
- **Changing one's own password** (`POST /api/account/password`) needs the current password, and is rate limited like sign-in. It's allowed while a password change is required, and signs out the account's other sessions; this one continues.

### 6.2 Sessions

- Sessions are server-side. The cookie (`NKeeper.Session`) carries only an encrypted reference to a row in `core.auth_sessions`. Signing out deletes the row, so a copied cookie stops working at once.
- The cookie is `HttpOnly`, `SameSite=Strict`, and `Secure` unless `Authentication:RequireSecureCookies` is turned off for local development.
- A session lasts 8 hours, or 14 days with "remember me", and slides while in use.
- The account is checked again on every request:
  - a disabled or deleted account gets `401`, and its session ends;
  - an account that must change its password gets `403` with `password_change_required`, except on the few endpoints that let it do that.

### 6.3 The key ring

- ASP.NET Core Data Protection encrypts the auth tickets and antiforgery tokens, and from step 4 the stored credentials.
- Its keys are kept in `core.data_protection_keys`.
- With `DataProtection:CertificatePath` (an RSA certificate and its private key, in PEM), the keys are encrypted with that certificate, so a copy of the database alone can't decrypt them.
- At startup, keys stored before the certificate was set are encrypted with it.
- The server refuses to start if the stored keys need a certificate that isn't configured, or a different one. Otherwise Data Protection would quietly make new keys, and everything already encrypted would become unreadable.

### 6.4 Request policies

These apply to everything under `/api`, plugin routes included. They are middleware, not endpoint filters, so they cover every kind of endpoint a plugin can map.

| Policy | Rule | Refusal |
| --- | --- | --- |
| Signed in | Required, except on `/api/auth/antiforgery`, `/api/auth/login` and `/api/auth/logout` | `401` |
| Antiforgery | Every request other than `GET`, `HEAD`, `OPTIONS` or `TRACE` carries the token from `GET /api/auth/antiforgery` in the header it names (`X-XSRF-TOKEN`). The token is bound to the user, so the client fetches a new one after signing in or out | `400` with `csrf_token_invalid` |
| Account status | §6.2 | `401`, or `403` with `password_change_required` |
| Sign-in rate | 10 attempts a minute per client address, for sign-in and for changing one's password | `429` with `rate_limited` and `Retry-After` |

Outside `/api`, the shell's files and pages, `/runtime/`, and `/health/live` need no sign-in. Plugin frontend files under `/plugins/` do.

Sign-ins, failed sign-ins (with the name as typed, never the password), sign-outs, and every change to an account (`user.create`, `user.update`, `user.password_reset`, `user.delete`, `account.password_change`) are written to `core.audit_events`.

## 7. Hosts and access

Ported from 1.x. Phase 1 step 3 brings hosts, their tags, and who may see and change them. Services, credentials, jump hosts and hardware details join a host in the steps that use them.

### 7.1 Hosts

- A host has:
  - a name (up to 200 characters);
  - a hostname (RFC 1123, underscores tolerated, up to 253 characters), an IP address (IPv4 or IPv6, stored in canonical form), or both. At least one is required;
  - an optional description (up to 4000 characters);
  - tags.
- **Tags** are a name (up to 64 characters, no colon, since `name:value` is the search syntax) and an optional value (up to 255). A host may carry one name with several values. Duplicates that differ only in case are dropped, keeping the first spelling. A host has at most 100 tags.
- **Only administrators create hosts.** Changing or deleting one needs Manage access to it (§7.2).
- **Edits can't silently overwrite each other.** Each host has a version (PostgreSQL's `xmin`). An update sends the version it was based on, and a stale one is refused with `409` and `version_conflict`.
- Creating, changing and deleting a host are audited (`host.create`, `host.update`, `host.delete`).

### 7.2 Access

- Administrators see and manage every host.
- Other users see only the hosts they're granted. A grant gives one of two levels: **Use** (see the host and connect to it) or **Manage** (also change and delete it). It covers either:
  - **one host**; or
  - **a group**: every host with a tag of that name (and, optionally, that value), compared without case. Group grants follow the tags, so a host tagged later is included at once.
- When grants overlap, the highest level applies.
- A host the user can't see answers `404`, exactly as if it didn't exist, so nothing is learned about hosts outside one's grants. A host the user can use but not manage answers `403` with `host_manage_denied` to a change.
- `HostAccess` in core is the one place that decides this. Every query for a user's hosts goes through it.
- Administrators set a user's grants with `PUT /api/users/{id}/grants`, which replaces them all, and read them with `GET`. Access is checked on every request, so a change applies at the user's next request. It's audited as `user.grants_replace`.
- Deleting a host deletes the grants for that host. Group grants stay, since they name tags, not hosts.

## 8. Where to read more

- [v2-plan.md](v2-plan.md): the audit of 1.x, the core/plugin boundary, the contribution catalogue, phases and decisions.
- [plugin-security.md](plugin-security.md): package format, signing, trust, directory checks, verification order and reason codes, loading and states.
- [CONTRIBUTING.md](../CONTRIBUTING.md): development setup and running the shell with plugins.
- `plugin-authoring.md` (Phase 1): writing a plugin, with SSH as the worked example.
