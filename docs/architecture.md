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
| **Kernel** | `core/backend/NKeeper.Core` | Core services every feature uses. Today: the audit log, hosts and access, services, credentials and the trust store (§7). |
| **Connectivity** | `core/backend/NKeeper.Connectivity.Ssh` | `IConnectivity` over SSH.NET: connections that core opens and authenticates, checked against the trust store, through jump hosts (§7.9). No endpoints, no service type. |
| **Identity** | `core/backend/NKeeper.Identity` | Accounts, sign-in, server-side sessions, the key ring and the request policies (§6). |
| **Server** | `core/backend/NKeeper.Server` | The composition root: startup order, `GET /api/plugins`, plugin frontend files, and the shell with its security policy. |
| **Shared runtime** | `core/frontend/runtime` (`@nkeeper/runtime`) | Builds the modules the shell shares with plugins, and the import map that names them. |
| **Shell** | `core/frontend/app` | The single-page app. Loads each plugin's frontend module. |
| **Tooling** | `tools/NKeeper.PluginTool` (`nkeeper-plugin`) | `keygen`, `pack`, `verify`. |
| **Plugins** | `plugins/<id>/` | One folder per plugin: backend, frontend, tests. None yet; SSH arrives in Phase 1. |
| **Test plugin** | `testing/plugins/NKeeper.TestPlugins.Hello` | A no-op plugin, built and packaged like a real one, used by the tests. |

The rest of core in [v2-plan.md §6](v2-plan.md#6-proposed-repository-structure) (the session framework, the UI kit) arrives during Phase 1.

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
- `Contribute(IPluginContributions, PluginContext)` declares what the plugin adds to core's catalogues: so far, service types (§7.4). It has an empty default, so a plugin with nothing to declare leaves it out.
- More contracts (`SessionType`, `HostAction`, `HealthProbe`, settings, audit actions) arrive with their first real users ([v2-plan.md §4.2](v2-plan.md#42-contribution-catalogue)). The SDK may change until it is frozen as v1 at the end of Phase 3.
- **SDK versions.** A manifest declares the SDK range it supports, and a package outside the server's `SdkVersion` is refused. Within v0, an addition raises the patch number (`0.1.1` added the host scope, `0.1.2` service types, `0.1.3` the credential broker, `0.1.4` SSH connections) and a breaking change raises the minor one. So a plugin built for `>=0.1.0 <0.2.0` keeps loading, and one that uses an addition declares the version that brought it.

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

Trust on first use, ported from 1.x: the first time a server presents an identity, the user checks it and decides; after that, core checks it on every connection. Phase 1 step 6 brings SSH host keys. TLS certificates join with their first user.

**What's trusted.**
- A trusted key belongs to one host. It's recorded per:
  - kind: an SSH host key, so far;
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
- A plugin can't trust a key. The shell posts the user's decision to core: `POST /api/hosts/{hostId}/services/{serviceId}/trusted-keys`, with the algorithm and fingerprint. It needs Use on the host.
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

**Failures.** Opening throws `SshConnectException` with a reason the plugin can show or act on:
- `HostKeyUnknown` (with the presented key) and `HostKeyChanged` (§7.7);
- `AuthenticationFailed`;
- `NoSecret`: the credential has neither a password nor a private key;
- `InvalidPrivateKey`: the key can't be read, or its passphrase is wrong;
- `Unreachable` and `TimedOut`;
- `JumpHostUnavailable`: the user can't use the jump host, or its service or credential is gone, or it wouldn't forward.

Messages name the host and port, never a secret.

## 8. Where to read more

- [v2-plan.md](v2-plan.md): the audit of 1.x, the core/plugin boundary, the contribution catalogue, phases and decisions.
- [plugin-security.md](plugin-security.md): package format, signing, trust, directory checks, verification order and reason codes, loading and states.
- [CONTRIBUTING.md](../CONTRIBUTING.md): development setup and running the shell with plugins.
- `plugin-authoring.md` (Phase 1): writing a plugin, with SSH as the worked example.
