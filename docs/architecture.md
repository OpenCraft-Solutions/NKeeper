# NKeeper 2.0 architecture

How NKeeper is put together today: the parts of core, how plugins plug in, and the rules that keep the two apart. The plan and its reasoning are in [v2-plan.md](v2-plan.md); how packages are signed, verified and loaded is in [plugin-security.md](plugin-security.md). This document describes what is built, and grows with each phase.

> **Core provides capabilities. Plugins consume capabilities and contribute functionality. Core never needs to know which plugins exist.**

## 1. The parts

| Part | Where | What it is |
| --- | --- | --- |
| **SDK (backend)** | `core/backend/NKeeper.Abstractions` | The contracts a plugin builds against: `IPlugin`, `PluginContext`, `SdkVersion`. Nothing else. |
| **SDK (frontend)** | `core/frontend/sdk` (`@nkeeper/sdk`) | `definePlugin` with session views, the API client (`api`, `ApiError`, `onUnauthorized`, `apiWebSocketUrl`, `readJsonLines`), `openStreamSession`, and the style helpers (§7.14). |
| **Packaging** | `core/backend/NKeeper.Plugins.Packaging` | The `.nkp` format, trust and signature verification: bytes in, a result out. No I/O of its own. |
| **Plugin host** | `core/backend/NKeeper.Plugins.Host` | Directory and trust checks, pinning, dependency resolution, one load context per plugin, enable state. |
| **Persistence** | `core/backend/NKeeper.Core.Persistence` | `CoreDbContext` (schema `core`), its migrations, and the stores the host writes plugin records and audit events to. |
| **Kernel** | `core/backend/NKeeper.Core` | Core services every feature uses. Today: the audit log, hosts and access, services, credentials and the trust store (§7). |
| **Connectivity** | `core/backend/NKeeper.Connectivity.Ssh` | `IConnectivity` over SSH.NET: connections that core opens and authenticates, checked against the trust store, through jump hosts (§7.9). No endpoints, no service type. |
| **Identity** | `core/backend/NKeeper.Identity` | Accounts, sign-in, server-side sessions, the key ring and the request policies (§6). |
| **Server** | `core/backend/NKeeper.Server` | The composition root: startup order, `GET /api/plugins`, plugin frontend files, and the shell with its security policy. |
| **Shared runtime** | `core/frontend/runtime` (`@nkeeper/runtime`) | Builds the modules the shell shares with plugins, and the import map that names them. |
| **Shell** | `core/frontend/app` | The single-page app: sign-in, the host tree, the host editor, tabs and core's dialogs (§7.14). Loads each plugin's frontend module. |
| **Tooling** | `tools/NKeeper.PluginTool` (`nkeeper-plugin`) | `keygen`, `pack`, `verify`. |
| **Plugins** | `plugins/<id>/` | One folder per plugin: backend, frontend, tests. The first is `ssh` (§7.11). |
| **Test plugin** | `testing/plugins/NKeeper.TestPlugins.Hello` | A no-op plugin, built and packaged like a real one, used by the tests. |

The rest of core in [v2-plan.md §6](v2-plan.md#6-proposed-repository-structure) arrives with its first users. The shell keeps its components to itself for now; a UI kit shared with plugins comes once a second plugin draws forms.

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
5. Call each loaded plugin's `Contribute`, in load order, and check what it declares (§7.4). A plugin whose contributions are refused (`contribution_invalid`), or whose `Contribute` throws (`load_failed`), is refused with its dependants, and contributes nothing.
6. Call each loaded plugin's `ConfigureServices`. If one throws, its registrations are rolled back, and it and the plugins that depend on it are refused (`load_failed`); the others carry on. With `Plugins:RequireAllEnabledLoaded`, any enabled plugin that didn't load stops startup instead.
7. Build the application and create the bootstrap administrator, if one is configured (§6.1). Then map core endpoints, each plugin's endpoints (`MapEndpoints`), and the shell's fallback route. A plugin that fails in `MapEndpoints` stops startup, because endpoints can't be unmapped.

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
- `endpoints.MapHostGroup(HostPermission.Use)` (or `Manage`) gives a group at `hosts/{hostId}/` whose endpoints act on one host the user may use (or manage). See §7.3.
- `Contribute(IPluginContributions, PluginContext)` declares what the plugin adds to core's catalogues: so far, service types (§7.4) with their health probes (§7.13), session types (§7.10) and host actions (§7.12). It has an empty default, so a plugin with nothing to declare leaves it out.
- More contracts (plugin settings, scanners, reports) arrive with their first real users ([v2-plan.md §4.2](v2-plan.md#42-contribution-catalogue)), as additions to SDK v1.
- **SDK versions.** A manifest declares the SDK range it supports, and a package outside the server's `SdkVersion` is refused. Within v0, an addition raises the patch number (`0.1.1` added the host scope, `0.1.2` service types, `0.1.3` the credential broker, `0.1.4` SSH connections, `0.1.5` stream sessions, `0.1.6` host actions, health probes and, in the frontend SDK, session views, `0.1.7` run actions and plugin audit events, `0.1.8` HTTPS connections, stateful sessions, session origins and, in the frontend SDK, `confirmTrust`, `0.1.9` desktop connections) and a breaking change raises the minor one.
- **SDK v1 (1.0.0)** froze the SDK at the end of Phase 3, after a review of what Phases 1 and 2 used ([v2-plan.md, Phase 3](v2-plan.md#phase-3-freeze-sdk-v1)). From v1, an addition raises the minor number and a breaking change the major one, which the plan avoids. So a plugin built for `>=1.0.0 <2.0.0` keeps loading, and one that uses an addition declares the version that brought it. The frontend SDK, `@nkeeper/sdk`, is versioned with it. [plugin-authoring.md](plugin-authoring.md) shows how to write a plugin against it.

## 5. Frontend extension point

**Shared modules.** The shell and every plugin use one copy each of `react`, `react/jsx-runtime`, `react-dom`, `react-dom/client` and `@nkeeper/sdk`. `@nkeeper/runtime` builds each as an ES module with a content hash in its name, under `/runtime/`, plus an import map naming them. The shell's build puts the import map first in `index.html`'s `<head>`. A plugin's bundle marks those modules external and imports them by name.

**A plugin's entry module** is the file its manifest names as `frontend.entry`. It default-exports a definition:

```ts
import { definePlugin } from '@nkeeper/sdk'

export default definePlugin({
  id: 'wake-on-lan',
  activate(context) {
    // anything the plugin sets up once, when the shell loads it
  },
})
```

The definition also declares what the plugin draws: so far, its session types' tabs (`sessionViews`, §7.14).

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

- ASP.NET Core Data Protection encrypts the auth tickets and antiforgery tokens, and from step 5 the stored credentials.
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

Ported from 1.x. Phase 1 step 3 brings hosts, their tags, and who may see and change them; step 4 brings their services, step 5 their credentials, and step 6 their jump hosts and trusted keys. Hardware details join a host in the steps that use them.

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

### 7.3 Host-scoped plugin endpoints

Host authorization is structural, not a convention ([v2-plan.md §4.4](v2-plan.md#44-guardrails-built-into-the-sdk)). A plugin acts on a host only through a group it gets from core:

```csharp
public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
{
    var host = endpoints.MapHostGroup(HostPermission.Use);          // /api/p/<id>/hosts/{hostId}/
    host.MapGet("/uptime", (HostContext target) => /* … */);
}
```

- Before any endpoint in the group runs, core checks the user's permission on the host in the route, through `HostAccess`. This is middleware, so it covers every kind of endpoint:
  - a host the user can't see (or that doesn't exist) answers `404`;
  - a host the user can only use answers `403` with `host_manage_denied` on a `Manage` group;
  - otherwise the request carries a **`HostContext`**: the host's ID, name, hostname, IP address and tags, and the user's permission on it.
- A route handler takes `HostContext` as a parameter. A raw request delegate calls `context.GetHostContext()`. Either way it fails outside a host group, so a plugin can't act on a host it was given by mistake.
- `HostContext` holds no secrets. It carries the host's services (§7.4), but never credentials: a plugin asks the broker for one and gets a handle, never a secret (§7.6).
- Plugins have no other way to reach hosts. There's no `IHostDirectory` yet: it arrives with its first user, a plugin that lists hosts (§4.2's rule).
- The authorization sweep covers these groups too. For every host-scoped endpoint, it checks that a user without a grant gets `404`, and that a Use-only user gets `403` on a `Manage` group.

### 7.4 Services and service types

A host offers **services**: SSH on port 22, a web interface, a database. Core knows no kinds of service itself. Each kind is a **service type** that a plugin contributes, so adding one never changes core (v2-plan.md §6.2).

```csharp
public void Contribute(IPluginContributions contributions, PluginContext context) =>
    contributions.AddServiceType(new ServiceType("ssh", "SSH")
    {
        DefaultPort = 22,
        Settings = [ServiceSetting.Text("shell", "Shell", maxLength: 200)],
    });
```

**Service types.**
- A type has:
  - an ID: lower-case letters, digits and `-`, starting with a letter, up to 32 characters;
  - a label;
  - an optional default port;
  - a settings schema, and the schema's version.
- **The settings schema** is a list of fields. Each has a key (a camel-case identifier), a label, and whether it's required. A field is one of:
  - text, with a maximum length (at most 4000);
  - a whole number, with an optional minimum and maximum;
  - a yes/no switch;
  - a choice among listed values.
- **Schema versions.** A type starts at version 1. A type that changes its settings raises the version and gives an `Upgrade(fromVersion, settings)` function: pure, and deterministic.
- Whether a type takes credentials, and other declarations, are added with the steps that use them, as additions to `ServiceType`.
- **Checked at startup.** The plugin host calls each loaded plugin's `Contribute` before its `ConfigureServices`, then checks what it declared. A plugin is refused, with its dependants (plugin-security.md §5.7, `contribution_invalid`), when:
  - an ID or field is malformed;
  - two fields share a key;
  - a version is below 1, or above 1 without an `Upgrade`;
  - it contributes a type ID that another plugin, earlier in load order, already contributed.

  A refused plugin registers nothing.
- `GET /api/plugins` lists each loaded plugin's service types, with their schemas, so the shell can draw a service's settings form.

**Services on a host.**
- A service has:
  - a type;
  - an optional label (up to 100 characters);
  - a port (1–65535). If it's left empty, the type's default applies;
  - an optional address, validated like a host's hostname or IP address. It's for a service that listens somewhere other than the host itself, such as a server's management controller. If it's left empty, the host's own address applies;
  - settings, checked against the type's schema. Unknown keys are refused, and missing optional fields stay unset.
- A host has at most 50 services, kept in the order given.
- Services are edited with their host: `services` in `POST` and `PUT /api/hosts`. It replaces the host's list:
  - a service sent with its `id` keeps it (credentials will refer to services by ID, step 5);
  - one sent without an `id` is new;
  - one left out is removed.

  It's audited with the host, as part of `host.create` or `host.update`.
- **Stored with their version.** Settings are stored as JSON, alongside the schema version they were written with:
  - on read, older settings are upgraded in memory with the type's `Upgrade`; reads never write (v2-plan.md §5);
  - a save writes the current version;
  - the startup step that rewrites old rows in batches arrives with the first type that reaches version 2.
- **A type no enabled plugin provides** (its plugin was disabled or removed):
  - its services are kept, and returned with `available: false` and their settings as stored;
  - such a service may stay through an edit unchanged, or be removed;
  - adding one, or changing one, is refused, since its settings can't be checked.
- `HostContext` carries the host's services, in order (SDK 0.1.2):
  - each one's ID, type, label and settings;
  - its effective port and address, with the defaults already applied;
  - whether its type is available.

### 7.5 Credentials

Ported from 1.x, which stores credentials the same way. Phase 1 step 5 stores them and brokers their use (§7.6), and core connects with them (§7.9).

**What a credential holds.**
- A credential belongs to one host. It has:
  - an optional label (up to 100 characters), username (up to 256) and domain (up to 256);
  - a password (up to 1024), which also serves as the passphrase of the private key when both are set;
  - a private key (up to 16 KB, in PEM or OpenSSH form);
  - notes (up to 2000).
- A host has at most 50 credentials, kept in the order given.

**Scoped to services.**
- A credential is used with all of the host's services, or with only some of them, as in 1.x.
- Its other services don't offer it, and the broker refuses it for them.

**Secrets are write-only.**
- Passwords and private keys are encrypted with the key ring (§6.3) before they're stored.
- The purpose string binds each ciphertext to its credential's ID, so a ciphertext copied to another row won't decrypt.
- No API response returns a secret, only `hasPassword` and `hasPrivateKey`.
- An update to a secret has three states:
  - **keep**: leave the field out;
  - **replace**: send a new value;
  - **clear**: send `clearPassword` or `clearPrivateKey`.

**Edited with their host.**
- Credentials are edited with their host, as `credentials` in `POST` and `PUT /api/hosts`, which replaces the list.
- A credential sent with its `id` keeps it, and keeps its stored secrets unless the request replaces or clears them. One sent without an `id` is new, and one left out is deleted.
- **Scoping.** A credential names the services it's used with by `serviceIndexes`: positions in the same request's `services` list, so it can name a service that's new in that request. Leaving the field out, or sending `null`, means all services. An empty list means none.
- **Responses.** A credential lists its services the same way, as positions in the response's `services`. A scope that pointed only at services since removed becomes empty, not "all".
- Anyone who can see the host sees its credentials without their secrets, so they can choose one to connect with. Changing them needs Manage.
- Changes are audited with the host, as part of `host.create` or `host.update`. The detail never includes a secret.

**Inside core.**
- A decrypted secret is a `Secret`:
  - it prints as `***`;
  - it can't be serialized: the JSON converter throws;
  - it clears its buffer when disposed.
- A secret never leaves core, except as §7.6 allows.

### 7.6 The credential broker

Plugins use credentials without seeing them (D13, [plugin-security.md §12](plugin-security.md#12-credentials)). The first use of a handle is an SSH connection that core opens (§7.9).

```csharp
use.MapPost("/connect", async (HostContext host, ConnectRequest request, ICredentialBroker broker, IConnectivity connectivity) =>
{
    var handle = await broker.ResolveSavedAsync(host, request.ServiceId, request.CredentialId);
    var who = await broker.DescribeAsync(host, request.ServiceId, handle);   // username, domain, source; never a secret
    await using var ssh = await connectivity.OpenSshAsync(host, request.ServiceId, handle);   // §7.9
});
```

**Resolving a credential (SDK 0.1.3).** `ICredentialBroker` resolves a credential for one service of a host the endpoint already holds as a `HostContext`. Two sources, each giving a **credential handle**:
- **A saved credential:** `ResolveSavedAsync(host, serviceId, credentialId)`. The credential must belong to the host and be used with the service.
- **A typed one:** `ResolvePromptAsync(host, serviceId, promptReference)`, from the core prompt below.

Passing on the user's own sign-in arrives with directory sign-in; local accounts have nothing to pass on.

**Handles.**
- A handle is opaque: 128 random bits. It prints as a placeholder and can't be serialized.
- Core keeps what the handle refers to, in memory only.
- A handle is bound to:
  - the user;
  - the host and service it was resolved for;
  - the plugin whose endpoint resolved it. Core knows the plugin from the endpoint's route group, not from anything the plugin says;
  - the request: a handle is dropped when the request that obtained it ends.

  Tying a handle to a plugin session instead arrives with sessions, in step 7. So does ending it when the user's access to the host changes.
- Every use of a handle checks all its bindings. `DescribeAsync`, and every connection opened with it (§7.9), takes the host and the service again. A mismatch, an unknown handle, or an expired one is refused with `CredentialDeniedException` and audited as `credential.denied`.
- Outside a plugin's own endpoint (a background job, say), the broker refuses to resolve. Credential use outside requests is designed with its first user.

**The credential prompt.**
- Typed credentials never pass through a plugin endpoint. The shell posts them to core: `POST /api/hosts/{hostId}/services/{serviceId}/credential-prompt`, with `username`, `domain`, `password` and `privateKey`. It needs Use on the host.
- Core keeps the secret in memory only, and answers with a **prompt reference**:
  - opaque, and single-use;
  - bound to the user's auth session, the host and the service;
  - it expires after 2 minutes.
- An auth session holds at most 10 unused prompt references; posting another drops the oldest.
- The shell sends the prompt reference to the plugin's endpoint, and the plugin hands it to `ResolvePromptAsync`.
- An exchange that fails for any reason (unknown, used, expired, another session, another host or service) is refused, like an unknown handle. A reference is used up when it's exchanged, even if the exchange is refused.
- Unused prompt references are dropped when they expire, and when their session signs out.

### 7.7 The trust store

Trust on first use, ported from 1.x: the first time a server presents an identity, the user checks it and decides; after that, core checks it on every connection. Phase 1 step 6 brought SSH host keys; TLS certificates joined with the web plugin (§7.16).

**What's trusted.**
- A trusted key belongs to one host. It's recorded per:
  - kind: an SSH host key, or a TLS certificate (§7.16);
  - endpoint: the service's own address, when it has one, or the host itself. So a management controller's key is never confused with its server's;
  - port;
  - algorithm (`ssh-ed25519`, say). A server presents one key per algorithm.
- The key itself is recorded as its SHA-256 fingerprint, unpadded base64 as OpenSSH prints it, with when and by whom it was trusted.
- Services of different types on the same endpoint and port share their trusted keys, because they're the same server.

**Checking a connection.** When core connects (§7.9) and the server presents a key:
- a trusted key for that algorithm that matches: the connection goes ahead;
- a trusted key that doesn't match: the connection is refused with `HostKeyChanged`, and audited as `trust.mismatch` with both fingerprints. Core never replaces a key by itself. Someone with Manage on the host removes the old key, and the next connection asks again;
- no trusted key: the connection is refused with `HostKeyUnknown`, carrying the presented algorithm and fingerprint, and the host and service that presented it (a jump host's, when it's the jump host that's unknown). Core remembers that it presented this key to this auth session.

**The user decides, through core.**
- A plugin can't trust a key. The shell posts the user's decision to core: `POST /api/hosts/{hostId}/services/{serviceId}/trusted-keys`, with the algorithm and fingerprint. It needs Manage on the host, since a trusted key vouches for the server to everyone who uses it: no access answers `404`, Use alone `403` with `trust_needs_manage`.
- Core trusts the key only if it presented exactly that key, for that host and service, to the same auth session, in the last 10 minutes. Anything else is refused with `400` and `trust_not_presented`, so a user (or a script with their cookie) can't trust a key no server showed them.
- Each presentation can be trusted once. An auth session keeps at most 20 presentations; recording another drops the oldest. They're dropped at sign-out.
- A different key trusted meanwhile for the same endpoint, port and algorithm answers `409` with `trust_conflict`.
- Trusting is audited as `trust.add`.

**Seeing and removing keys.**
- A host's response lists its trusted keys: ID, kind, address (empty for the host itself), port, algorithm, fingerprint and when they were trusted.
- `DELETE /api/hosts/{hostId}/trusted-keys/{keyId}` removes one. It needs Manage, and is audited as `trust.remove`.
- Keys are deleted with their host. Changing a service's address or port leaves them in place, under the endpoint they were trusted for.

### 7.8 Jump hosts

Ported from 1.x. A host can be reached through another host, its **jump host**: core connects to the jump host over SSH, then tunnels to the host through it.

**On the host.**
- `jump` in `POST` and `PUT /api/hosts`: the jump host's ID, the service to reach it on, and the credential to sign in to it with. `null` means none.
- When it's set:
  - the jump host must exist, and the editor must be able to use it. One they can't see is refused like a missing one;
  - it can't be the host itself;
  - only one level: a jump host can't have its own jump host, and a host that's another's jump host can't be given one;
  - the service and the credential must be the jump host's, and the credential must be used with that service (§7.5).
- Changes are audited with the host.
- Deleting the jump host clears `jump`. Removing its service or its credential leaves the host pointing at the jump host without them, and connecting fails until the host is edited.

**Connecting through it** (§7.9).
- The user must be able to use the jump host too, checked at every connection, so a jump host can't lend its credential to someone who can't see it.
- Core reads the jump host's credential itself. It never reaches a plugin, not even as a handle.
- The jump host's key is checked like any other (§7.7). An unknown one is refused with `HostKeyUnknown` naming the jump host's service, so the user can trust it there and try again.
- The target's own key is still checked against the target, not the jump host, so a jump host can't pose as the host behind it.
- The jump host must allow TCP forwarding.

### 7.9 SSH connections

Core opens the connection and the plugin gets it authenticated (D13 tier 1, plugin-security.md §12.3). Over SSH.NET, in `NKeeper.Connectivity.Ssh`.

```csharp
use.MapPost("/uptime", async (HostContext host, UptimeRequest request, ICredentialBroker broker, IConnectivity connectivity) =>
{
    var handle = await broker.ResolveSavedAsync(host, request.ServiceId, request.CredentialId);
    await using var ssh = await connectivity.OpenSshAsync(host, request.ServiceId, handle);
    var result = await ssh.RunAsync("uptime");
    return Results.Text(result.Output);
});
```

**Opening (SDK 0.1.4).** `IConnectivity.OpenSshAsync(host, serviceId, handle)`:
- checks the handle's bindings, as the broker does (§7.6). A refusal is a `CredentialDeniedException`, audited as `credential.denied`;
- connects to the service's effective address and port, through the host's jump host if it has one (§7.8);
- checks the server's key against the trust store (§7.7);
- signs in with the credential: the private key first, if there is one (with the password as its passphrase), then the password, also through keyboard-interactive. A domain is sent as `DOMAIN\user`, as Windows OpenSSH expects;
- gives up after 20 seconds, and sends keep-alives every 30 once connected.

Every connection opened is audited as `credential.use`: the host, service, plugin, the credential (or "typed"), and the jump host's credential if any.

**What a plugin gets.** An `ISshConnection`, not SSH.NET's client, whose connection details include the secrets. It offers:
- `RunAsync(command)`: runs a command, and returns its exit status, output and error output;
- `OpenShellAsync(terminal)`: an interactive shell with a terminal type and size, as a stream, which can be resized.

SFTP and port forwards join with their first users. The connection doesn't need its handle once it's open; the plugin disposes it, which also closes its tunnel. Closing connections when the user's access changes arrives with sessions, in step 7.

**Failures.** Opening throws `ConnectException` with a `ConnectFailure` the plugin can show or act on. Every kind of connection core opens (SSH here, HTTP in §7.16, desktops in §7.20) throws the same exception, with the reasons that apply to it. For SSH:
- `HostKeyUnknown`, with the presented identity, and `HostKeyChanged` (§7.7). A `PresentedIdentity` names the host and service that presented it, its kind (`SshHostKey` or `TlsCertificate`), algorithm and fingerprint, and for a certificate its subject and expiry. As JSON it's the frontend SDK's `TrustPrompt`, so a plugin endpoint can return it for its view to pass to `confirmTrust` (§7.14);
- `AuthenticationFailed`;
- `NoSecret`: the credential has neither a password nor a private key;
- `InvalidPrivateKey`: the key can't be read, or its passphrase is wrong;
- `Unreachable` and `TimedOut`;
- `JumpHostUnavailable`: the user can't use the jump host, or its service or credential is gone, or it wouldn't forward.

Messages name the host and port, never a secret.

### 7.10 Stream sessions

A terminal is a **stream session**: one WebSocket that stays open while the user works ([v2-plan.md §7.1](v2-plan.md#71-session-framework-core)). Core runs everything up to the moment the plugin's data starts flowing, so every session type gets the same checks, prompts, limits and audit. Ported from 1.x's `SessionSocket` and `SessionLimiter`. Stateful sessions (a database query window, say) arrive with their first user.

**Session types (SDK 0.1.5).**

```csharp
public void Contribute(IPluginContributions contributions, PluginContext context) =>
    contributions.AddSessionType(new SessionType("ssh", "Terminal", typeof(TerminalSessionHandler)) { ServiceTypes = ["ssh"] });
```

- A session type has:
  - an ID (the same rules as a service type's, in its own namespace);
  - a label;
  - the service types it opens on: at least one;
  - its handler type, an `IStreamSessionHandler` the plugin registers in `ConfigureServices`;
  - whether it can go through a jump host (yes by default).
- It's checked at startup like a service type (§7.4). A plugin is refused, with `contribution_invalid`, for a malformed ID or label, no service types, or an ID another plugin took first.
- A service type it names may come from another plugin. When no enabled plugin provides it, the session type simply has nothing to open on.
- `GET /api/plugins` lists each plugin's session types.

**The protocol.** `GET /api/sessions/{type}`, upgraded to a WebSocket. Control messages are JSON in text frames, each with a `type`:

| Direction | Message | When |
| --- | --- | --- |
| browser → core | `start` `{ hostId, serviceId, credentialId?, promptReference?, options? }` | first, within 15 seconds |
| core → browser | `status` `{ status: "connecting" }` | after `start` is accepted |
| core → browser | `hostKey` `{ hostId, serviceId, kind, algorithm, fingerprint, subject?, notAfterUtc? }` | a server presented an unknown key, or (`kind: "tls-certificate"`) certificate (SDK 0.1.9) |
| browser → core | `hostKeyDecision` `{ trust }` | the user's answer, within 2 minutes |
| core → browser | `connected` | the plugin's session is open |
| core → browser | `error` `{ code, message }` | it failed; the socket then closes |
| core → browser | `closed` `{ reason }` | it ended after connecting |

After `connected`, the plugin owns the data: binary frames, and text frames whose `type` isn't one of core's. `options` is the plugin's own (a terminal's size, say), passed to it as JSON.

**What core does, in order.**
1. **Checks the request.** The user is signed in, and the `Origin` header is the server's own, since browsers don't send antiforgery tokens with WebSockets. Otherwise `403`, before upgrading.
2. **Checks the limit.** A user has at most 20 stream sessions open, of every type together. One more is refused with `too_many_sessions`.
3. **Reads `start`.** It refuses:
   - a message that isn't valid, with `bad_request`;
   - a host the user can't use, with `host_not_found`, exactly like a missing one;
   - a service that isn't the host's, or isn't of one of the session type's service types, with `wrong_service`;
   - a host reached through a jump host, with `jump_host_unsupported`, when the type can't use one.
4. **Resolves the credential**, if one is named: a saved one by `credentialId`, or a typed one by `promptReference` (§7.6). The handle is bound to the session type's plugin and to this request, which lasts as long as the session. A refusal is `credential_denied`, audited as `credential.denied`.
5. **Opens the plugin's session**, with the `HostContext`, the service, the handle and the options: `IStreamSessionHandler.OpenAsync`. The plugin connects, through `IConnectivity`, which can't tell this request from a plugin endpoint's. The session is in the registry (below) from this step on, so a change of access while it opens, during a key prompt say, ends it with `access_revoked`.
6. **Asks about unknown keys in-band.** If opening fails with `HostKeyUnknown`, from an SSH connection or (SDK 0.1.9) a desktop or HTTP connection's certificate, core sends `hostKey` and waits for the user, if they can manage the host that presented it (§7.7). If they can't, the session ends with `host_key_untrusted`, saying who must trust it first. On `trust`, it trusts the key through the trust store, under the same rules as §7.7, then opens again. A jump host and its target can each ask once. Anything but `trust`, including silence, ends the session with `host_key_rejected`.
7. **Reports other failures** with a code from the failure: `host_key_changed`, `authentication_failed`, `no_secret`, `invalid_private_key`, `unreachable`, `timeout` or `jump_host_unavailable`. Anything else the plugin throws is `failed`, with a generic message, and is logged.
8. **Runs the session.** Core checks access once more, audits `session.open`, sends `connected`, and calls `IStreamSession.RunAsync` with a channel for the plugin's frames. It ends when either side closes, or when core ends it.

Core limits each message to 1 MB, and sends WebSocket keep-alives every 30 seconds.

**A plugin can't hold a session open.** When the browser goes, or core ends the session, the plugin's token is cancelled. A plugin's `RunAsync` or `DisposeAsync` that hasn't returned 10 seconds later is left behind and logged, and core finishes without it. Core's own closing messages have a 5-second deadline, after which it aborts the socket.

**Ending sessions when access changes.** Checking access only when a session opens isn't enough, since NKeeper is a gateway to privileged infrastructure (v2-plan.md §7.1). Every open session is in core's **session registry**, with its user, sign-in session, host, service, credential and the jump host it went through. Core re-checks the sessions involved:
- at once, when:
  - a user's grants are replaced;
  - a host is edited (its tags, services or credentials) or deleted;
  - a user is disabled, deleted, or loses or gains a role, or their password is reset;
  - a user signs out;
- and every 30 seconds, for anything else (a sign-in session that expired, say).

A session is still allowed while:
- its sign-in session is live;
- its user is enabled and can use its host;
- its service is still the host's;
- its saved credential, if it used one, is still the host's;
- the host's jump host is still the one it went through, and the user can still use that jump host. Changing a host's route ends its sessions, since their tunnels stay on the old one.

Otherwise core ends it:
- the plugin's `RunAsync` is cancelled;
- the browser gets `closed` with `access_revoked`;
- the socket closes;
- the handle and its secrets go with the request.

This fixes a 1.x gap: 1.x ended sessions when a user was disabled, but not when their grants or a host's tags changed.

**Audit.**
- `session.open`: host, session type, service, and the credential (or "typed").
- `session.close`: the reason (`closed`, `access_revoked`, `failed`) and how long it lasted.

### 7.11 The `ssh` plugin

The first real plugin, in `plugins/ssh/`. It's a plugin like any other: it references only the SDK, and core doesn't name it.
- **Service type** `ssh`, port 22 by default, no settings yet, checked by a TCP probe (§7.13).
- **Session type** `ssh`, "Terminal", on `ssh` services:
  - `options`: the terminal's `columns` and `rows`;
  - it opens a shell through `IConnectivity` (§7.9), on an `xterm-256color` terminal;
  - binary frames carry the terminal's bytes both ways, and a text frame `resize` `{ columns, rows }` resizes it. Dimensions that aren't numbers fall back to 80 × 24.
- **Host action** `ssh.terminal`, "Terminal", which opens it with a credential (§7.12).
- **Its frontend** is the terminal view (§7.14): xterm.js, fitted to its tab, resizing the server's terminal as the tab resizes. When the shell exits it says so, and offers to reconnect.

**Built and signed by the build.** Plugins are always signed, even in development (plugin-security.md §10):
- building `plugins/ssh` builds its frontend (`npm run build` in `plugins/ssh/frontend`, so `npm ci` must have run at the root), packs both with `nkeeper-plugin`, and signs the package with the developer's development key. The build creates the key on first use, under `.dev/`, which git ignores;
- the package goes to `artifacts/plugins/`, the directory to point `Plugins:Directory` at in development;
- CI verifies the package against that run's development key.

### 7.12 Host actions

The shell's menus are built from data that plugins contribute, not from menu code (v2-plan.md §7.2). The first kind of entry is **Connect**: open one of the plugin's session types on a service.

```csharp
contributions.AddHostAction(new HostAction("ssh.terminal", "Terminal", SessionType: "ssh"));
```

- A host action (SDK 0.1.6) has:
  - an ID: like a service type's, plus dots between segments (`ssh.terminal`), in its own namespace;
  - a label;
  - the session type it opens, which must be one the same plugin contributes. It applies to the services that session type opens on;
  - its credential mode: `Required` (the default), meaning a saved credential or a typed one; or `None`.
- It's checked at startup like the plugin's other contributions (§7.4). It's refused with `contribution_invalid` for a malformed ID or label, a session type the plugin doesn't contribute, or an ID taken first.
- `GET /api/plugins` lists each plugin's host actions.
- **The shell builds the Connect menu** for a host from every loaded plugin's actions. An action offers one entry per service whose type its session type opens on and is available. A host behind a jump host gets none when the session type can't use one. With `Required`, each entry offers:
  - each saved credential used with that service;
  - "Enter credentials…", which asks for one in core's dialog when the session opens (§7.6).

  The plugin only names the session type; it never sees the menu or the credential.
- Other locations (Tools, Tasks), and actions that open a dialog of the plugin's own, arrive with their first users.

**Run actions (SDK 0.1.7).** An action can run one of the plugin's endpoints instead of opening a session:

```csharp
contributions.AddHostAction(new HostAction("wake-on-lan.wake", "Wake") { Endpoint = "wake", ServiceTypes = ["wake-on-lan"], Credential = HostActionCredential.None });
```

- A host action names exactly one of a session type or an `Endpoint`. A run action names the service types it applies to (at least one). An `Endpoint` is a path of lower-case segments, relative to the plugin's host group.
- The shell calls it as `POST /api/p/<plugin>/hosts/<hostId>/<endpoint>`, with `{ serviceId }`, plus `credentialId` or `promptReference` when the action needs a credential. The plugin maps that endpoint in a host group (`MapHostGroup(HostPermission.Use)`), so core authorizes it like any plugin endpoint (§7.3).
- The endpoint answers `{ message }`, which the shell shows. A refusal is a problem response, and the shell shows its detail.
- Run actions are listed in the same menu as Connect, each under its own heading. Double-clicking a host opens only a session, never a run action, so nothing runs by accident.

### 7.12.1 Plugin audit events

Plugins record their own operations in core's audit trail (v2-plan.md §4.2, SDK 0.1.7):

```csharp
await audit.RecordAsync("wake-on-lan.send", host.Id, $"{mac} to {targets}");
```

- `IPluginAudit` is available to a plugin's endpoints. Core knows which plugin is calling from the endpoint's route group, as the credential broker does (§7.6), not from anything the plugin says.
- An action's name must be the plugin's ID, a dot, then lower-case letters, digits, `-` and `_` (`wake-on-lan.send`). So a plugin can't record an event that looks like core's (`host.update`) or another plugin's. Anything else is refused with an `ArgumentException`.
- The event records the signed-in user and the client's address, like core's own. Outside a plugin's endpoint (a background job, say), recording is refused until a first user needs it.
- Core still records every lifecycle and security event itself (sessions, credentials, trust), so a plugin can't leave those out.

### 7.13 Health probes

A service type can say how to tell whether a service is up (SDK 0.1.6):

```csharp
new ServiceType("ssh", "SSH") { DefaultPort = 22, Health = HealthProbe.Tcp }
```

- `HealthProbe.Tcp` is the only probe so far: a TCP connection to the service's effective address and port, closed at once. Nothing is sent, and no credential is used. Other probes (a TLS certificate, a protocol handshake) arrive with their first users. A type without a probe isn't checked.
- **Core runs the probes**, never the plugin, so a plugin can't reach out on a schedule of its own:
  - in the background, every `Health:Interval` (5 minutes by default, at least 1), unless `Health:Enabled` is false;
  - each check has `Health:Timeout` (5 seconds by default, at most 60), and at most `Health:Parallelism` (32 by default) run at once;
  - an address and port that two services share is checked once;
  - a host reached through a jump host isn't checked, since it usually can't be reached from the server. Its services have no status.
- **Results are kept in memory only.** For each service: `up` or `down`, how long the check took, and when it ran. For a service that's down, also why: `refused`, `timeout` or `unreachable`. After a restart, services have no status until their next check.
- `GET /api/health` returns the results for the hosts the user can see. `POST /api/hosts/{hostId}/health` checks one host now, and needs Use. Checking a host isn't audited, since it reads nothing from it.

### 7.14 The shell

The single-page app in `core/frontend/app`. It knows no service type, session type or plugin: everything it shows of them comes from `GET /api/plugins`.

**The look is 1.x's.** The shell looks the same as NKeeper 1.x, so moving to 2.0 changes nothing a user sees except what's new:
- 1.x's design tokens, as CSS custom properties on `:root`: the dark navy colours, spacing, radii, shadows and font stack. Its UI components (buttons, fields, dialogs, menus, badges, alerts) and their styles are ported from 1.x as they were.
- 1.x's layout: a top bar with the logo and the pages as tabs, **Hosts**, **Reports**, and for administrators **Users** and **Settings**, with the user's menu on the right. Each page has 1.x's sidebar on the left and its content on the right.
- A control for a feature that isn't ported yet (Import, Scan network, sorting, colour schemes) appears when its feature does, rather than as a control that does nothing.
- **Plugin views use the same tokens.** `var(--color-surface)` and the rest are the shell's to keep, so a plugin's view matches the shell. Each use gives a fallback value, so a view still draws on a shell without them. The terminal and desktop views keep 1.x's session styles.

**Layout.**
- **Sign-in**, on 1.x's sign-in card, then the top bar with the user's menu, which holds Sign out. A session that ends elsewhere brings the sign-in form back (unchanged from Phase 0). A user who must change their password (§6.1) is asked to before anything else.
- **The host tree**, on the left:
  - every host the user can see, from `GET /api/hosts`, searched, grouped and sorted in the browser;
  - **search** matches a host's name, hostname, IP address, description and tags (`name` or `name=value`), ignoring case;
  - **group by** a tag name gives one group per value, plus "No <name>" for hosts without it. The other choice is no grouping. The choice is kept in the browser;
  - each host shows its services' health (§7.13). The shell checks a host when it's created or saved, rather than waiting for the next background round;
  - selecting a host shows its details. Its Connect menu (§7.12) is on the host's row and in its details;
  - right-clicking a host's row opens 1.x's host menu there: Connect, Edit and Copy. Double-clicking it opens the menu's first entry: the first action on the first service it applies to, with the first saved credential used with that service, or the typed-credential dialog when there's none;
  - a host's details say why there's nothing to connect with: it has no services, or no loaded plugin opens a session on them.
- **Tabs**, on the right. The first tab is always the selected host's details. Every session opens in a tab of its own. A tab stays mounted while another is showing, so a terminal keeps running. Closing a tab ends its session.
- **Reports**, for everyone: 1.x's list of reports on the left, with the report on the right. So far it lists one:
- **Health**, ported from 1.x's health report: the latest checks (§7.13) of the services on the hosts the user can see, from `GET /api/health` and `GET /api/hosts`:
  - a summary: how many services are up and down, how many certificates expire within 30 days, how many hosts with services have no results yet, and when the latest check ran;
  - **Down**: each service that's down, with its host, address and port, why, and when it was checked, oldest first;
  - **Certificates**: each certificate a TLS probe read, with its host, service, subject and expiry, soonest first. One that has expired or expires within 30 days is marked;
  - selecting a host's name shows it in the workspace. Refresh reads the results again; it doesn't run the checks.
- **Settings**, for administrators: 1.x's settings page. So far it has one section, **Plugins**: the plugin list and frontend states from Phase 0.
- **Users**, for administrators (§6.1, §7.2):
  - the list, with each user's role and whether they're disabled, locked out, or must change their password;
  - creating a user, with a user name, email, role, and an initial password, which they must change at first sign-in unless the administrator says otherwise;
  - editing a user's email, role and whether they're disabled; resetting their password; and deleting them. An administrator can't disable, demote or delete themselves, as the server enforces;
  - a user's grants: rows of a host, or a tag name with an optional value, each at Use or Manage. Saving replaces them all. Grants don't apply to administrators, who see every host, so the page says so for them.

  The workspace stays mounted behind these pages (Reports, Users and Settings), so sessions carry on.

**The host editor** creates and edits hosts, for users who can manage them:
- name, hostname, IP address, description and tags;
- services: a type from the loaded service types, an optional label, port and address, and a **settings form drawn from the type's schema** (§7.4). The form has text, number, switch and choice fields, with their limits. A service whose type no plugin provides is shown as it's stored, and can only be kept or removed;
- credentials: label, username, domain, and the secrets. Secrets are write-only: a credential shows whether it has a password or a key, and the editor can replace or clear them. Each credential is used with all services or chosen ones;
- the jump host: another host, with one of its services and credentials;
- the server keys the host trusts (§7.7), each removable;
- saving sends the version the edit started from. On a conflict, the editor says someone else changed the host, and reloads it.

The server's own validation is what counts. The editor shows its field errors next to their fields.

**Core's dialogs.** These belong to the shell, not to any plugin:
- **a server key no one trusted yet** (§7.10): the host, algorithm and fingerprint, with "Trust and connect" and Cancel, worded as 1.x's trust card was. Only someone with Manage on the host sees it (§7.7);
- **a typed credential** (§7.6): username, domain, and password or private key. The shell posts it to core's credential prompt and passes only the prompt reference on.

**Session views (frontend SDK 0.1.6).** A plugin's frontend draws its session types' tabs:

```ts
export default definePlugin({
  id: 'ssh',
  sessionViews: { ssh: TerminalView },
})
```

- `sessionViews` maps a session type that the same plugin contributes to a React component. The shell ignores a view for any other session type.
- The component gets:
  - the host's ID and name, and the service's ID, as the tab describes them;
  - `connect(options, signal)`, which opens the stream. It runs core's control phase, with core's dialogs for a key and a typed credential. It resolves to a connection once core says `connected`, and rejects with a `SessionError` carrying core's error code. Aborting `signal` (when the view goes) cancels it, and any dialog it was waiting on;
  - whether its tab is showing;
  - `confirmTrust(prompt)` (SDK 0.1.8), for a view that opens its session through the plugin's own endpoints (§7.17): it shows core's dialog for a key or certificate a plugin endpoint reported, and when the user trusts it, posts it to core. It resolves to whether it's now trusted. For a user who can't manage the host, it resolves to false without asking.
- A connection sends binary frames, JSON text frames, and (SDK 0.1.9) raw text frames, and delivers the plugin's frames. Its `closed` promise gives core's reason. The view may call `connect` again to reconnect.
- The SDK's `openStreamSession` implements the protocol (§7.10). `connect` is that function, with the shell's dialogs.
- **Styles under the shell's policy.** The policy allows stylesheets only from the server (`style-src 'self'`), so a `<style>` element that a library adds is blocked. The SDK has two helpers that keep the policy as it is:
  - `adoptStyles(css)` adds a stylesheet from text, as a constructable stylesheet, which the policy allows;
  - `mirrorInjectedStyles(element)` copies the `<style>` elements that a library adds under `element` (xterm.js does) into constructable stylesheets. It keeps them in step until it's called off.

### 7.15 The `wake-on-lan` plugin

The smallest real plugin, in `plugins/wake-on-lan/`: it shows how little a plugin needs, and it changes no core file (v2-plan.md, Phase 2). Ported from 1.x's `Health/WakeOnLan`.
- **Service type** `wake-on-lan`, "Wake-on-LAN", UDP port 9 by default, with one required setting: the MAC address. Hosts in 1.x had a MAC address field; in 2.0, a host gets Wake-on-LAN by having this service, so core keeps no field for it.
- **Run action** `wake-on-lan.wake`, "Wake", on `wake-on-lan` services, with no credential (§7.12).
- **Its endpoint** `POST /api/p/wake-on-lan/hosts/{hostId}/wake` needs Use. It sends the magic packet from the NKeeper server (six `0xFF` bytes, then the MAC address 16 times) over UDP to the service's port, to:
  - the local broadcast address;
  - for a service with an IPv4 address, its `/24` subnet's broadcast address and the address itself, for routers that forward directed broadcasts or keep a static ARP entry.

  A MAC address that isn't six hexadecimal bytes (with `:` or `-` between them) is refused with `400`.
- It records `wake-on-lan.send` (§7.12.1), with the MAC address and where the packet went.
- No frontend: the shell's run actions are enough.

### 7.16 HTTPS connections and certificate trust

Core opens HTTP connections for plugins, as it does SSH (D13), so certificate trust is core's, and no plugin can skip it (SDK 0.1.8).

```csharp
await using var http = await connectivity.OpenHttpAsync(host, serviceId, tls: true);
var response = await http.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(http.BaseAddress, "/")), ct);
```

- `IConnectivity.OpenHttpAsync(host, serviceId, tls)` connects to the service's effective address and port, and gives the plugin an `IHttpConnection`: an `HttpMessageInvoker` that sends every request to that endpoint and nowhere else, with its base address.
  - It sends no cookies of its own, follows no redirects, and doesn't decompress, so a proxy can pass responses on as they came.
  - **Through a jump host** (§7.8), core opens an SSH connection to the jump host with its credential, which core reads itself, as for SSH (§7.9), and forwards a loopback port through it to the service. The user must be able to use the jump host. Its key must already be trusted, since a request can't stop to ask: an SSH session to the jump host asks once. Otherwise it fails with `JumpHostUnavailable`, saying so. The service's certificate is still checked against the service, so the jump host can't impersonate it. Opening the tunnel is audited as `credential.use`.
- **Certificates** (when `tls` is true):
  - one that's publicly valid for the address is accepted, as in 1.x;
  - otherwise only a certificate the user trusted for the host is: the trust store (§7.7) gains a second kind, `TlsCertificate`, recorded per endpoint and port. Its "algorithm" is `x509`, and its fingerprint the SHA-256 of the certificate, in the same unpadded base64;
  - core checks before it hands the connection over. A handshake that shows an untrusted certificate fails with `ConnectException` and `HostKeyUnknown`, carrying the certificate's fingerprint, subject and expiry, and core remembers it presented it, as for SSH keys. A different certificate than the trusted one fails with `HostKeyChanged`, audited as `trust.mismatch`.
  - every later request on the connection is checked against the same rule, so a certificate that changes mid-session is refused too.
- The user trusts a certificate the way they trust a key: the shell posts it to core's trusted-keys endpoint, which takes the kind from what core presented. The shell's dialog says "certificate" and shows its subject and expiry.

**TLS health probe.** `HealthProbe.Tls`: a TCP connection and a TLS handshake that accepts any certificate, so an expired or self-signed one still counts as up. The result adds the certificate's subject and expiry, and the host's details show how long it has left.

### 7.17 Stateful sessions

Some sessions live in the plugin rather than on a WebSocket: a web session is a run of HTTP requests through the plugin's proxy. Core still owns their limits, revocation and audit (v2-plan.md §7.1), through `IPluginSessions` (SDK 0.1.8):

```csharp
using var session = await sessions.OpenAsync(host, serviceId, sessionType: "web");   // from the plugin's host endpoint
// … later, on every request: if (session.Ended.IsCancellationRequested) refuse
```

- `OpenAsync` registers the session in core's session registry (§7.10) for the signed-in user, the request's auth session, the host, the service and the jump host, and audits `session.open`. It can only be called from the plugin's own host-scoped endpoint, for the host core authorized, and only for a session type the plugin contributes.
- It counts toward the user's 20 sessions, stream sessions included. Over the limit, it throws `SessionLimitException`, and the plugin answers `409` with `too_many_sessions`.
- Core re-checks it with every other session, and ends it when access changes: its `Ended` token is cancelled, with the reason (`access_revoked`). The plugin then refuses its requests.
- Disposing it unregisters it and audits `session.close` with the reason: `closed`, `access_revoked`, or what the plugin gives (`idle`).
- A session type that's stateful says so: `SessionType.Stateful(id, label)`, with no stream handler. Core opens no WebSocket for it; its view (§7.14) talks to the plugin's own endpoints.

### 7.18 Session origins

A web session's pages are served on an origin of their own, so the site can never read NKeeper's cookies or call its API (ported from 1.x, SDK 0.1.8):

```csharp
contributions.AddSessionOrigin(new SessionOrigin("nkw", typeof(WebOriginHandler)));
```

- **The web domain** is an install setting, `Origins:BaseDomain` (`setup.sh --web-domain`), because it needs wildcard DNS, a certificate and nginx to match. Without it, session origins are off, and a plugin that needs one says so.
- A plugin claims a **prefix**: lower-case letters and digits, 2 to 16 of them, unique across plugins. Core sends every request for `<prefix>-<label>.<web domain>`, where the label is 1 to 63 lower-case letters and digits, to the plugin's `ISessionOriginHandler.HandleAsync(context, label)`.
- Core does this first, before its own pipeline: those requests never reach NKeeper's sign-in, antiforgery, API, shell or security headers, and NKeeper's cookies are never sent to them, since they're scoped to NKeeper's own host. The plugin's handler is the whole of what answers there.
- `ISessionOrigins.UrlFor(prefix, label)` gives a plugin the origin's URL (`https://nkw-<label>.<web domain>`), or null when there's no web domain.
- The shell's policy allows frames from `https://*.<web domain>` when one is set, and from nowhere else (`frame-src`).

### 7.19 The `web` plugin

Web interfaces through NKeeper, in a tab: ported from 1.x's `Web/` (YARP).
- **Service types** `http` (port 80, TCP probe) and `https` (port 443, TLS probe), as 1.x had them.
- **Session type** `web`, "Web page", stateful (§7.17), on both, through a jump host too (§7.16).
- **Host action** `web.open`, "Web page", with no credential.
- **Opening** (`POST /api/p/web/hosts/{hostId}/sessions`, Use): core opens the connection (§7.16) and registers the session (§7.17). The plugin answers with a one-time launch URL on the session's origin (§7.18), valid for 2 minutes. An untrusted certificate answers `409` with `host_key_unknown` and the certificate as `presented`, for the shell's dialog; after the user trusts it, the view opens again.
- **The session origin** `nkw-<session ID>.<web domain>`, where the ID is 128 random bits:
  - the launch link sets the session's cookie (HttpOnly, Secure, `SameSite=None`, Partitioned, since NKeeper frames it) and redirects to `/`;
  - every other request must carry that cookie. Without it, or for an unknown or ended session, the handler shows a short page instead;
  - requests go to the target through core's connection, with `Host`, `Origin` and `Referer` rewritten to the target's, NKeeper's cookie removed, and redirects and cookies rewritten to stay on the session origin. Responses may be framed only by NKeeper's own origin;
  - a session idle for 30 minutes ends, as does one whose access ends (§7.17).
- **Closing** (`DELETE /api/p/web/hosts/{hostId}/sessions/{id}`): the view calls it when its tab closes.
- **Its frontend** is the `web` session view: it opens the session, asks the shell to trust a certificate when it must, and shows the site in a frame.
- A capability for other plugins to open web sessions (1.x's iDRAC console) arrives with its first user, in Phase 4.

### 7.20 Desktop connections

Remote desktops go through guacd, Apache Guacamole's proxy daemon, which speaks RDP and VNC to the server and the Guacamole protocol to NKeeper (D11). Core makes the connection, as it does SSH and HTTP (D13): guacd needs the credential's secrets, and a plugin never sees them (SDK 0.1.9).

```csharp
await using var desktop = await connectivity.OpenDesktopAsync(host, serviceId, handle, new DesktopOptions(Width: 1280, Height: 800, Dpi: 96));
// desktop.ReceiveAsync: guacd's instructions for the browser; desktop.SendFromBrowserAsync: the browser's, filtered
```

- `IConnectivity.OpenDesktopAsync(host, serviceId, handle, options)`, for an RDP service, so far:
  - checks the handle, as for SSH (§7.9), and audits `credential.use`;
  - **checks the server's certificate first.** It reads the certificate RDP offers (an X.224 negotiation, then a TLS handshake), and checks it like an HTTPS one (§7.16): publicly valid, or the one trusted for the host. An unknown one fails with `ConnectException` and `HostKeyUnknown`, carrying the certificate, which core remembers it presented. A changed one fails with `HostKeyChanged`, audited as `trust.mismatch`;
  - through a jump host, opens a tunnel (§7.16), and guacd connects to its loopback end;
  - connects to guacd (`Guacd:Address`, `127.0.0.1:4822` by default) and does the Guacamole handshake itself: it selects `rdp`, sends the browser's size, and the connection's parameters. These are the address, port, user name, domain and password, NLA security, and `cert-fingerprints` set to the certificate it just checked, so guacd accepts exactly that one. The browser can never choose the protocol, the target or a parameter;
  - returns once guacd says `ready`, with an `IDesktopConnection`:
    - `ReceiveAsync` gives guacd's instructions for the browser, `ready` first, as Guacamole text;
    - `SendFromBrowserAsync` takes the browser's instructions and passes on only what **1.x's allow-list** (`GuacamoleInstructionFilter`) allows. The filter is core's, in the connection, so no plugin can skip it:
      - input, display and plain-text clipboard only: `key`, `mouse`, `touch`, `size`, `sync`, `ack`, `nop`, `clipboard`, `blob`, `end`, `disconnect`;
      - stream data only for clipboard streams the browser opened, at most 16 of them, of at most 1 MB of text each;
      - everything else is dropped: file uploads, audio input, argument changes, and anything unknown. It's a security control on a relay that's reachable from the browser, so it has its own negative tests;
      - text that doesn't parse as instructions throws, and the session ends.
- **Options** come from the plugin, and are checked: the size (between 200 × 200 and 8192 × 8192), DPI, the audio and image types the browser takes, and its time zone.
- guacd that isn't running fails with `GatewayUnavailable`.
- **The Guacamole protocol code** (parser, encoder, handshake) is core's, ported from 1.x's `GuacamoleProtocol` and `GuacdConnection`.

### 7.21 The `desktop` plugin

RDP through guacd, as a proof that the SDK carries a third kind of session (v2-plan.md, Phase 2): ported from 1.x's `Desktop/`. VNC, sign-in pass-through and clipboard settings follow in Phase 4.
- **Service type** `rdp`, port 3389, TLS probe (§7.13).
- **Session type** `rdp`, "Desktop", a stream session (§7.10) on `rdp` services, through jump hosts too.
- **Host action** `rdp.desktop`, "Desktop", with a credential.
- **The session** relays Guacamole instructions between the browser and core's desktop connection (§7.20), as text frames: the browser's through `SendFromBrowserAsync`, so through core's allow-list.
- `options`: the browser's size, DPI and time zone.
- **Its frontend** is the `rdp` session view: Guacamole's own JavaScript client (`guacamole-common-js`, Apache 2.0), bundled with the plugin, drawing into the tab and scaling to it.

## 8. The native install

`deploy/setup.sh` installs NKeeper on a Linux server, from a checkout of the repository, as root. It's an early version of the Phase 6 installer (v2-plan.md §8). NKeeper installs natively only (D8): systemd, nginx and PostgreSQL, no containers.
- **Supported:** Debian 12 and later, Ubuntu 24.04 and later (apt), and the RHEL 9 family and Fedora (dnf), on x64 and arm64.
- **Commands:**
  - `install`, which also upgrades an existing install;
  - `uninstall`, which keeps the database and `/etc/nkeeper`. `--purge` removes them too.

**What `install` does, in order.**
1. Installs PostgreSQL, nginx and OpenSSL from the distribution.
2. Sets up PostgreSQL: a role and database `nkeeper` with a random password, and password sign-in over loopback only.
3. **Builds NKeeper** from a copy of the checkout, so the checkout gets no root-owned files:
   - with the .NET 10 SDK and Node.js, fetched into `/var/cache/nkeeper-build`. Node.js is checked against its published SHA-256;
   - the server is published self-contained for the machine's architecture;
   - the shell and the built-in plugins are built as in development.
4. **Signs the built-in plugins** with a one-time install key (plugin-security.md §6.1), and deletes its private key.
5. Lays out the files below, and writes the settings file (with `Origins:BaseDomain` when given `--web-domain`, §7.18), the key ring certificate (on a fresh install only), the systemd unit and nginx's site. nginx gets a self-signed certificate unless one is given (`--cert`, `--key`).
6. **Starts NKeeper.** On a fresh install it creates the first administrator: the installer asks for a password, or generates one and prints it once. Once the server is up, it turns the bootstrap off and removes the password from the settings file.

| Path | Owner / mode | Contents |
| --- | --- | --- |
| `/usr/lib/nkeeper/server/` | `root:root` `0755` | the server |
| `/usr/lib/nkeeper/shell/` | `root:root` `0755` | the shell's files (`Frontend:Root`) |
| `/usr/lib/nkeeper/plugins/` | `root:root` `0755` | the built-in plugin packages, `0644` |
| `/etc/nkeeper/plugin-trust/` | `root:root` `0755` | the install key's public key file, `0644` |
| `/etc/nkeeper/nkeeper.env` | `root:root` `0600` | the settings, with the database password. systemd reads it as root |
| `/etc/nkeeper/data-protection.pem` | `root:nkeeper` `0640` | the key ring certificate (§6.3). Back it up apart from the database |
| `/etc/nkeeper/tls/` | `root:root` `0700` | nginx's certificate and key |
| `/var/lib/nkeeper/` | `nkeeper:nkeeper` `0750` | the only place the service writes |

**The service** (`deploy/systemd/nkeeper.service`) runs the server as the unprivileged `nkeeper` user, listening on `127.0.0.1:5080` only. It has the hardening of plugin-security.md §4: `ProtectSystem=strict` with only `/var/lib/nkeeper` writable, `NoNewPrivileges`, `PrivateTmp`, `ProtectHome` and an empty `CapabilityBoundingSet`. It also has `PrivateDevices`, the kernel protections, `RestrictSUIDSGID`, `LockPersonality`, and network families limited to IPv4, IPv6 and Unix sockets.

**nginx** (`deploy/nginx/nkeeper.conf`), with HTTP/2 turned on in the form the installed version expects:
- terminates TLS 1.2 and 1.3 on port 443, and redirects port 80 to it. With a web domain, it also answers `*.<web domain>`, and its self-signed certificate covers that wildcard. The wildcard DNS record is the administrator's to make;
- proxies everything to the server, WebSocket upgrades included, with read timeouts long enough for idle sessions. It serves no files itself (plugin-security.md §7.3);
- sends `X-Forwarded-For` and `X-Forwarded-Proto`, and HSTS.

**Forwarded headers.** With `ForwardedHeaders:Enabled`, which the installer sets, the server takes the client's address and scheme from `X-Forwarded-For` and `X-Forwarded-Proto`. It accepts them from a proxy on the loopback address only, for one hop. Without this, the server would see nginx's plain-HTTP request from `127.0.0.1`. Then the WebSocket origin check (§7.10) would refuse every session, the sign-in rate limit (§6.4) would count every user as one client, and the audit would record no real addresses.

**guacd, as a native service** (D11). Remote desktops need guacd (§7.20). Distributions don't ship guacd 1.6, so the installer builds it, with the FreeRDP it runs on:
- **FreeRDP 2.11.7**, built first, the version Apache's own guacd images use. It's built with its own MD4, which NLA's NTLM sign-in needs and OpenSSL 3 leaves out by default. guacd 1.6.0 crashes on FreeRDP 3.31 and newer, a known upstream bug ([FreeRDP#13342](https://github.com/FreeRDP/FreeRDP/issues/13342)), and the RHEL 10 family ships 3.32. Building it everywhere gives guacd the same, known-good FreeRDP on every distribution, and the distribution's own FreeRDP is left alone;
- **guacamole-server 1.6.0**, against that FreeRDP, with RDP only: no SSH, telnet, VNC or Kubernetes (VNC comes with Phase 4);
- both from their source releases, checked against SHA-256 hashes pinned in the installer. guacamole-server's was checked against its release signature by Apache's code-signing key when it was pinned. FreeRDP's was checked against two separately hosted copies, pub.freerdp.com and its GitHub release, which were identical;
- into `/usr/lib/nkeeper/guacd/`, root-owned, rebuilt only when either version changes;
- on the RHEL family, a few build packages come from CRB, which the installer enables;
- it runs as `nkeeper-guacd.service`: its own unprivileged user, listening on `127.0.0.1:4822` only, with the same hardening as NKeeper's service plus `ProtectProc`. It can write only its private `/tmp` and its state directory, `/var/lib/nkeeper-guacd`, which is its home, since FreeRDP keeps its settings there;
- `--no-guacd` skips it, and a build that fails never stops NKeeper from installing. Either way, remote desktops then say the gateway is unavailable.

**SELinux** (RHEL family): the installer labels the server's executable as a program systemd may start, and lets nginx connect to the server.

**Tested in CI, through a real browser.** The Install workflow runs the installer on a fresh Ubuntu runner. Then a headless Chromium (Playwright, `testing/e2e`):
- signs in, and creates a host for the runner's own SSH server;
- opens a terminal with a typed credential and with a saved one, and trusts the server's key;
- runs commands, checks the terminal resizes with the window, and deletes the host;
- opens a web page on the server itself, trusts its certificate, and sees it framed on its session origin (installed with `--web-domain web.localhost`, since Chromium resolves `*.localhost` to loopback);
- opens a remote desktop to xrdp on the runner, through the guacd the installer built, trusts its certificate, and sees the desktop draw.

It then upgrades in place and checks again.

The rest of the installer is Phase 6: release-signed packages, guacd (D11), and install and upgrade tested end to end on both distribution families.

## 9. Where to read more

- [v2-plan.md](v2-plan.md): the audit of 1.x, the core/plugin boundary, the contribution catalogue, phases and decisions.
- [plugin-authoring.md](plugin-authoring.md): writing a plugin against SDK v1, with ssh and wake-on-lan as worked examples.
- [plugin-security.md](plugin-security.md): package format, signing, trust, directory checks, verification order and reason codes, loading and states.
- [CONTRIBUTING.md](../CONTRIBUTING.md): development setup and running the shell with plugins.
