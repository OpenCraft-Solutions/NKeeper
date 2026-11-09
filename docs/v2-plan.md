# NKeeper 2.0: audit and modular architecture plan

Status: revision 4, decisions recorded (§9), no code written yet.
Source audited: [NKeeper 1.x](https://github.com/DHemken97/NKeeper) at commit [`04de1dc`](https://github.com/DHemken97/NKeeper/commit/04de1dc) (PR #39).

> **Guiding principle.** Core provides capabilities. Plugins consume capabilities and contribute functionality. Core must never need to know which plugins exist to work correctly.

**Revision 2 changes** (after an external review):
- SSH transport moves to its own project, `NKeeper.Connectivity.Ssh`, and the boundary is stated (§3.1).
- The SDK starts small, and each extension point arrives with its first real user (§4.2).
- Plugin dependencies are split into kinds (§4.5).
- Host authorization is structural rather than by convention (§4.4).
- Plugin data carries schema versions, and secrets stay write-only (§5).
- The phases are reordered: minimal core plus SSH first, deployable from Phase 1, and a 1.x export fixture early (§8).
- Runtime third-party plugins would run out of process (§8, Phase 8).

**Revision 3 changes** (second review):
- The 1.x importer is split: core reads bundles, and each plugin maps its own legacy data (§7.4).
- Sessions are bound to owner, host and permission, and access changes end sessions that are no longer allowed. 1.x doesn't do this when grants or tags change (§7.1).
- Settings upgrades no longer write on read (§5).
- Phase 2 proves desktop sessions without requiring full desktop parity (§8).
- Session lifecycle audit events are core-owned (§4.2).

**Revision 4 changes** (decisions recorded, §9):
- **Plugins load at run time** from signed packages, with root-only installation, per-file hash and signature verification, and version pinning. The security design is in [plugin-security.md](plugin-security.md) and summarized in §7.6. This also changes §5, §6, §6.1 and Phase 0.
- **Native Linux only:** no IIS, no Docker or container deployment. Docker stays a development and CI dependency for the test containers only (§8, D8).
- **No upgrade path from 1.x for now** (D10). The `import-v1` plugin and the Phase 1 export fixture are removed. Instance transfer between 2.0 servers stays (§7.4).
- D11 (providing guacd without Docker) and D12 (custody of the official signing key) are decided: a native guacd service first, with a container fallback; two copies of the release key.

---

## 1. Summary

NKeeper 1.x does a lot, and does it carefully: a host inventory with tags and encrypted credentials, browser sessions for about 20 kinds of service (SSH, RDP, VNC, web, KVM/Proxmox consoles, iDRAC, four database engines), Linux and Windows admin tools, VM migrations that move storage too, Zabbix, scans, health checks, audit, SSO, and an instance export/import.

All of it lives in one API project and one frontend feature tree. Each kind of service is spread across both tiers through a central `ServiceProtocol` enum and hand-written `switch`/ternary chains. To add a service type you have to edit about 12 files in both tiers, and every feature can reach every other feature's internals.

**2.0 keeps the stack (.NET 10, React 19, PostgreSQL, guacd) and the security model. It changes the shape**:

- a small **core** owns everything that is about trust: identity, access, the credential vault, audit, the session framework, connectivity (SSH transport and jump hosts), and host-key/certificate pinning;
- everything that is about a *kind of thing you connect to* becomes a **plugin**: a vertical slice (backend + frontend + tests + docs) that registers through declared extension points;
- the database-engine layer that already exists in 1.x (`IDatabaseConnector`, `IDatabaseDialect`, `DatabaseReports`, `tools.ts`) is the model. It is already a small plugin system, and 2.0 applies the same pattern everywhere.

**Plugins are loaded at run time from signed packages** (decision D1). A plugin runs inside the server with access to decrypted credentials, so loading one is treated as installing code into NKeeper:
- only root on the server can install a package;
- every file is hash-checked and the package signature verified before any code loads;
- an NKeeper administrator can only enable what root installed.

See §7.6 and [plugin-security.md](plugin-security.md).

---

## 2. Audit of 1.x

### 2.1 Size and shape

| Area | Files | Lines (approx.) | Notes |
| --- | --- | --- | --- |
| Backend API (`NKeeper.Api`) | ~150 `.cs` | ~30k | One project: controllers, protocols, tools, integrations, scans |
| Backend infrastructure | ~35 `.cs` + 27 migrations | ~4k + migrations | EF Core, Identity, entities, settings |
| Backend tests | ~70 | ~15k | Real servers in Testcontainers plus fake CLI scripts. A strength. |
| Frontend (`src/`, excluding vendor) | ~230 | ~22k | React feature folders |
| Scripts / deploy | setup.sh/.ps1, compose, nginx, single image | ~3k | Docker, native Linux, IIS |

Largest hotspots: `WorkspacePage.tsx` (1,264 lines), `HostManager.cs` (937), `GuestMovers.cs` (873), `StorageHost.cs` (757), `SftpController.cs` (704), `hostMenu.ts` (592), `HostContracts.cs` (553).

### 2.2 Feature inventory: what has to survive into 2.0

| Domain | Feature | 1.x location (backend / frontend) |
| --- | --- | --- |
| **Identity** | Local accounts, AD (LDAPS), OIDC SSO, email 2FA, server-side sessions, bootstrap admin | `Infrastructure/Identity`, `Security/`, `AuthController` / `features/auth`, `account` |
| **Access** | Admin/user roles; grants per tag-group or host at Use/Manage | `Access/HostAccess.cs`, `Users/` / `features/users` |
| **Inventory** | Hosts, services, credentials (per-service), tags with inheritance, clone, bulk edit, search, group-by, limits | `Inventory/HostManager.cs`, `Contracts/Inventory` / `features/inventory` |
| **Import** | mRemoteNG XML (incl. its AES-GCM encryption) | `Inventory/Import` |
| **Scanning** | Internal (SSH script), external (ports), Windows (WMI via impacket), ARP neighbours, guest/cluster matching | `Inventory/Scanning` / `ScanDialog`, `BulkScanDialog`, `scanProposals.ts` |
| **Health** | Periodic service checks, TLS expiry, Wake-on-LAN | `Health/` / `HealthReport`, tree dots |
| **Sessions: terminals** | SSH (with recording), Telnet (auto-login) | `Ssh/`, `Telnet/`, `RemoteSessions/` / `SshTerminal` |
| **Sessions: desktops** | RDP, VNC through guacd; RDP cert pinning; AD sign-in pass-through | `Desktop/` / `DesktopSession`, vendored Guacamole |
| **Sessions: web** | YARP proxy on per-session origins `nkw-<id>.<domain>` | `Web/` / `WebSession` |
| **Linux tools (over SSH)** | SFTP files, Journal, SELinux, Storage (iSCSI, multipath, DRBD) | `SftpController`, `Journal/`, `Selinux/`, `Storage/` |
| **Windows tools** | RPC session, Event Log Viewer, WMI scan | `Rpc/`, `EventLogs/`, `*.py` helpers |
| **Virtualization** | KVM (virsh) VM list, power, SPICE/VNC consoles; Proxmox API consoles, tasks; storage-aware migrations | `Kvm/`, `Proxmox/`, `Migration/` / `VirtualMachinesView`, `MigrateDialog`, `GuestTaskDialog` |
| **Hardware** | iDRAC console, Redfish status/power, RACADM | `Idrac/` / `IdracView` |
| **Databases** | MS SQL (query, explorer, blocking, orphans), Oracle, SAP ASE, Replication Server; DBA reports; Copy Database; drift compare | `Databases/`, `SqlServer/`, `Oracle/`, `Ase/` / `features/databases` |
| **Integrations** | Zabbix (link, overview, comparison report, import) | `Zabbix/` / `features/zabbix` |
| **Reports** | IP map, VM capacity, inventory, health | `features/reports` |
| **Audit** | Audit trail, retention, terminal recordings and playback | `Auditing/` / `features/audit` |
| **Admin** | Settings (email, limits), diagnostics log/monitor, instance transfer (encrypted bundle), colour schemes, shortcuts | `Diagnostics/`, `Transfer/`, `features/settings`, `transfer`, `appearance` |
| **Security plumbing** | TOFU pinning (SSH keys, RDP/HTTPS/SQL certs), CSRF, rate limits, CSP, credential encryption, origin checks | `RemoteSessions/TrustedKeyStore`, `Web/CertificateTrust`, `Security/` |

### 2.3 What 1.x does well (carry over unchanged in spirit)

1. **Secrets never reach the browser.** `SessionTargetResolver` decrypts in memory on the server only. This stays a core invariant.
2. **One choke point for authorization.** `HostAccess.Visible/Manageable` is the only way to load hosts. In 2.0 plugins never get a raw `DbSet<Host>`. They get this.
3. **Trust on first use**, keyed per host/port/protocol, is consistent across SSH, RDP, HTTPS, SQL Server and iDRAC.
4. **Contracts are separate from entities** (`Contracts/`), and responses never expose EF types.
5. **Tests against real servers** (OpenSSH, xrdp, guacd, SQL Server, Postgres, fake Redfish/Zabbix/OIDC) plus fake CLI scripts with state. This is the most valuable asset after the domain knowledge itself.
6. **The database layer** (`DatabaseEngine` → `IDatabaseConnector` + `IDatabaseDialect` + report registry + `tools.ts` + one generic `DatabaseView`) is already a working plugin pattern.
7. **Pure planners** (`MigrationPlanner`, `ZabbixMatcher`, `InternalScan.Parse`) separated from I/O, which makes them easy to test.
8. **No orphaned connections**: keep-alive pings, idle expiry, `pagehide` cleanup.
9. **Committed migrations**, invariant culture, warnings as errors.

### 2.4 What blocks modularity (the problems 2.0 solves)

| # | Problem | Evidence | 2.0 answer |
| --- | --- | --- | --- |
| P1 | **Service types are a closed enum** referenced in 31 backend files, and mirrored in frontend string unions | `ServiceProtocol` in `Infrastructure/Inventory/Host.cs`; `SUPPORTED_PROTOCOLS`, `PROTOCOL_INFO` in `types.ts`; `SessionProtocol` union in `terminal/types.ts` | Service types are string IDs registered by plugins (`"ssh"`, `"mssql"`). Core never switches on them. |
| P2 | **Adding a service touches ~12 files in both tiers** | enum, `SupportedServices`, `HostHealth.DefaultPorts`, `ScanPorts`, `SessionTargetResolver`, `HostContracts`, mRemoteNG mapping, `types.ts`, `hostMenu.ts`, `WorkspacePage.tsx` (protocol→session map and tab render chain), `ServicesEditor` | One plugin folder. Defaults, health probe, editor fields, menu entries, tab renderer and import mapping are all contributions. |
| P3 | **Protocol-specific columns on shared entities** | `HostService.RdpSecurity`, `.Address` (iDRAC), `.Instance` (SQL/Oracle); `Host.ZabbixHostId`, `VirtualMachineId` string format `proxmox:`/`libvirt:` parsed by regex in the frontend | `HostService.Settings` (jsonb, validated by the owning plugin's schema). Plugin-owned host data in plugin tables or `HostExtension` rows. |
| P4 | **Tab dispatch is a 20-branch ternary** | `WorkspacePage.tsx` lines ~1085–1160; protocol→tab mapping at ~127–143 | A tab registry: `sessionType → lazy component`. |
| P5 | **Menus are hand-built per feature** | `hostMenu.ts`: `TOOL_PROTOCOLS`, `NOT_THROUGH_JUMP`, `WEB_PROTOCOLS`, iDRAC modes, guest regexes | Declarative menu contributions plus one generic builder (credentials submenus, jump-host rules). |
| P6 | **One composition root registers everything** | `ApiServiceCollectionExtensions.AddNKeeperApi` (≈60 registrations, rate-limit policies naming specific controllers) | Each plugin has its own `ConfigureServices` / `MapEndpoints`. Core applies auth, CSRF, audit and rate limits to the plugin's route group. |
| P7 | **Special cases leak into shared code** | RACADM-as-SSH-on-iDRAC inside `SessionTargetResolver`; `SessionChecks.RefuseJumpHost`; `ServiceTools` (files/storage/journal/selinux/eventlog) hard-coded in the *Infrastructure* entity file | Capabilities: a session type declares `SupportsJumpHost`, and a plugin can declare a *derived endpoint* (iDRAC → SSH:22 at the service's address). Tools are registered against a service type. |
| P8 | **Integration settings are core tables** | `ZabbixSettings`, `EmailSettings`, `InventoryLimits` DbSets in the main context | Typed plugin settings store (jsonb + protected secret fields), or plugin-owned schema. |
| P9 | **Sessions are in-process only** | documented limitation | 2.0 keeps in-process sessions but puts them behind `ISessionStore` so scale-out stays an option (not a 2.0 goal). |
| P10 | **Runtime prerequisites are global** | guacd required by compose; Python + impacket bundled for WMI on Linux | Plugins declare requirements. Diagnostics shows unmet ones. A disabled plugin needs nothing. |
| P11 | **Large files mix concerns** | `HostManager` (validation + persistence + cloning + bulk), `SftpController` (I/O inside the controller), `WorkspacePage` (tabs, tree, dialogs, keyboard) | Split along the new seams. Controllers become thin endpoint maps over services. |

---

## 3. Architecture overview

```
┌───────────────────────────────────────────────────────────────────────────┐
│ Browser                                                                   │
│  App shell (layout, tree, tabs, dialogs, keyboard)                        │
│   └─ Plugin registry ← plugins' frontend modules (tabs, menus, panels…)   │
└───────────────▲───────────────────────────────────────────────────────────┘
                │ HTTPS / WebSocket (single origin + nkw-* web origins)
┌───────────────┴───────────────────────────────────────────────────────────┐
│ NKeeper.Server (ASP.NET host: composition root only)                     │
│                                                                           │
│  ┌──────────────────────── CORE (kernel) ──────────────────────────────┐  │
│  │ Identity & auth providers │ Access (HostAccess) │ Audit & recordings │  │
│  │ Inventory (hosts, tags, services, credentials) │ Vault (encryption)  │  │
│  │ Session framework (stream + stateful, limits, keep-alive, stores)    │  │
│  │ Connectivity (SSH transport, jump tunnels, port forwards, scripts)   │  │
│  │ Trust store (TOFU) │ Health scheduler │ Jobs │ Settings │ Transfer   │  │
│  │ Plugin host (verify signed packages, load, dependencies, enable)     │  │
│  └──────────────────────────────▲──────────────────────────────────────┘  │
│                                 │ NKeeper.Abstractions (the plugin SDK)   │
│  ┌──────┬──────┬────────┬──────┴─┬────────┬────────┬────────┬─────────┐   │
│  │ ssh  │telnet│desktop │  web   │ linux  │storage │windows │  kvm …  │   │
│  └──────┴──────┴────────┴────────┴────────┴────────┴────────┴─────────┘   │
└───────────────────────────────────────────────────────────────────────────┘
        │                    │                     │
   PostgreSQL           guacd (only if         target hosts
 (core schema +        desktop plugin on)
  plugin schemas)
```

### 3.1 Core vs plugin: the rule

> **Core is what every plugin must trust and none may reimplement. A plugin is anything that knows about one specific product or protocol.**

Core does **not** know about any *service type*: no SSH terminal, SQL Server or Zabbix. It knows SSH only as a **transport**, the way it knows TCP. It does know how to:

- load a host the user may see, and decrypt a credential for one service of it;
- open a TCP or SSH connection to a host, through its jump host if it has one;
- pin and check a key or certificate;
- run a long-lived session (WebSocket or keep-alive REST) with limits and cleanup;
- record an audit event, store settings, run a background job, schedule a health check.

**One judgement call:** SSH *transport* (SSH.NET connection, script runner, port forward, jump tunnel) sits in core, even though SSH the *terminal* is a plugin. Jump hosts, KVM, storage, journal, SELinux, internal scans, migrations, RACADM and Proxmox consoles all ride on SSH. If SSH transport were a plugin, nearly every other plugin would depend on it, and jump hosts (a core inventory concept) would too.

The boundary is explicit. `NKeeper.Connectivity.Ssh` is a separate core project that implements the generic `IConnectivity` capability (`OpenTcp`, `OpenSsh`, `RunScript`, `ForwardPort`, jump tunnels). It is shared infrastructure with no UI, no service type and no endpoints. The `ssh` *plugin* registers the SSH service type and terminal session and consumes `IConnectivity` like any other plugin. HTTP is handled the same way: core offers a pinned `HttpClient` factory that uses the trust store, and it is infrastructure, not the `web` plugin.

### 3.2 Alternative considered: SSH, RDP and inventory as core features

The alternative: keep SSH, RDP and inventory inside the core app, and make storage, iDRAC, MS SQL, Oracle, Zabbix and the rest add-on plugins. **The product boundary matches this plan: those are the parts every install gets, and the add-ons are optional.** The difference is how the built-in features are *built*.

| | A. Built into core code | B. Bundled plugins (recommended) |
| --- | --- | --- |
| What ships | inventory + SSH + RDP always | same: SSH, RDP/VNC, web, Telnet ship in every build and are marked `builtIn` (can't be disabled) |
| Where the code lives | `core/` | `plugins/ssh`, `plugins/desktop`… written against the same SDK as add-ons |
| Proves the SDK | only add-ons use it; the hardest cases (stream sessions, TOFU, recordings, credential prompts, sign-in pass-through) are never exercised through it | the hardest cases go through the SDK first, so add-ons inherit a tested path |
| Drift risk | core keeps private shortcuts (as 1.x's RACADM-in-resolver or `NOT_THROUGH_JUMP`), and add-ons become second-class | everything uses one path; nothing in core names a service type |
| Dependency direction | add-ons that need SSH (storage, journal, KVM, RACADM) depend on core internals | they depend on core *connectivity* (SSH transport), which is a public SDK service |
| Cost | less up-front design | slightly more ceremony for the first three plugins (paid in Phase 2) |

**Both designs keep SSH *transport* (connections, scripts, port forwards, jump tunnels) in core**, because jump hosts and half the add-ons need it. The question is only whether the SSH *terminal* and RDP *desktop* sessions are core code or bundled plugins. B gives the product shape of A while keeping core free of protocol code. Inventory is core in both.

---

## 4. Extension points (the plugin SDK)

`NKeeper.Abstractions` holds the contracts only. Signatures are illustrative.

### 4.1 Plugin descriptor

```text
IPlugin
  Manifest            id ("mssql"), name, version, sdkVersion, dependsOn ["db-core"],
                      requirements [guacd | python-impacket | …], description
  ConfigureServices(IPluginServices)       DI registrations, options binding
  MapEndpoints(IPluginEndpoints)           routes under /api/p/{id}/… (core applies auth/CSRF/rate limits)
  Contribute(IPluginContributions)         everything below
```

A matching **frontend module** exports `definePlugin({ id, ... })`. The server's `GET /api/plugins` returns the enabled plugins and their declarative contributions. The frontend joins them with its local components by key.

### 4.2 Contribution catalogue

**This table maps where every 1.x feature will fit. It is not a build list.** SDK v0 contains only what Phases 0–2 exercise:

| SDK v0 contract | First real user |
| --- | --- |
| `IPlugin` (manifest, dependencies, lifecycle) | no-op plugin, then ssh |
| `ServiceType` (incl. settings schema + version) | ssh, web |
| `SessionType` (stream + stateful) | ssh (stream), web (stateful) |
| `HostAction` (menu) | ssh, web, wake-on-lan |
| `MapEndpoints` (host-scoped route groups) | web, wake-on-lan |
| `HealthProbe` | ssh (TCP), web (TLS) |
| `ISettingsStore<T>` (plugin settings, secrets) | web (base domain) |
| `AuditActions` | wake-on-lan (`wake-on-lan.send`). Plugins declare only their own operations; see below. |

**Audit ownership.** Core owns and emits every lifecycle and security event, so each session plugin is audited the same way without doing anything:
- `session.open`, `session.close` (with a reason: `user`, `idle`, `access_revoked`, `admin`, `shutdown`), `session.denied`;
- `access.denied`, `credential.use`. The last one is for non-session credential use such as scans and background jobs. A session's credential is recorded in `session.open`, so it isn't written twice.

Plugins declare only events describing their own operations, such as `mssql.kill`, `storage.action`, `vm.power` and `migration.run`.

Every other row below is added in the phase that ports its first real user, and is designed from that user's needs: Scanner with `linux`, ServiceTool with `linux`, Sub-registries with `db-core` / `virt-core`, HostDataExtension and Report with `zabbix`, Importer and TransferParticipant in Phase 5, AuthProvider when AD/OIDC are ported. The SDK is versioned (`sdkVersion` in each manifest) and frozen as v1 at the end of Phase 3. Plugins may reference `NKeeper.Abstractions` only, never `NKeeper.Core` internals, and an architecture test enforces this.

| Extension point | What it declares | 1.x example it replaces |
| --- | --- | --- |
| **ServiceType** | id, label, default port, settings schema (fields, validation), whether it takes credentials and whether username is required, import aliases | enum member + `PROTOCOL_INFO` + `HostService.Instance/Address/RdpSecurity` |
| **ServiceTool** | a tool offered on a service type (id, label, default on/off) | `ServiceTools.For(Ssh) = [files, storage, journal, selinux]` |
| **SessionType** | id, kind (`stream` WebSocket / `stateful` store), `supportsJumpHost`, idle timeout, per-user limit, recorded? | `SshTerminalSession`, `DatabaseSessionStore`, `ConsoleSessionStore`, `NOT_THROUGH_JUMP` |
| **Endpoint derivation** | "this service can also be reached as X at Y" | RACADM special case in `SessionTargetResolver` |
| **HostAction** (menu) | location (`connect`, `connect/tools`, `tasks`, `vm`), applies-to (service type, tool, host predicate), credential mode (saved / prompt / none / sign-in), opens (session type + params) or runs (dialog key) | `hostMenu.ts` |
| **HealthProbe** | how to check a service (TCP, TLS cert, RDP X.224, custom) | `HostHealth` switch |
| **Scanner** | kind (internal via SSH / via RPC / external), applies-to; returns a `ScanContribution` (services, hardware, guests, neighbours, cluster nodes, notes) | `InternalScan`, `ExternalScan`, `WindowsInternalScan`, `OracleDatabases`, `SqlServerInstances` |
| **HostDataExtension** | per-host data a plugin owns, its detail-panel section, editor field | `Host.ZabbixHostId` + `ZabbixSection` + `ZabbixHostField` |
| **Importer** | file format → host drafts (preview, then add/update/delete) | `MRemoteNgFile`, `HostImporter` |
| **Report** | page id, title, data endpoint, CSV export | IP map, VM capacity, Zabbix comparison, health |
| **SettingsPanel** | admin settings section with a typed settings object (secret fields encrypted) | Email, Zabbix, Limits panels |
| **AuditActions** | action names, labels, categories for the audit filter | string literals like `storage.action`, `mssql.kill` |
| **TransferParticipant** | export/import its own data in the instance bundle | `InstanceExporter` hard-codes every table |
| **BackgroundService / Job** | hosted service or user-owned job with steps and rollback | `MigrationJobs`, `HealthMonitor`, sweepers |
| **AuthProvider** | sign-in method (local, LDAP, OIDC) and second factor (email code; TOTP/WebAuthn later) | `DirectoryAuthenticator`, `SingleSignOn`, `TwoFactorSignIn` |
| **Sub-registries** | a plugin can expose its own extension point to dependants | `db-core` exposes `IDatabaseEngine` (connector, dialect, reports, tools) for mssql/oracle/ase; `virt-core` exposes `IGuestProvider` for kvm/proxmox |

### 4.3 Core services a plugin may consume

`IHostDirectory` (HostAccess-filtered reads, never a raw DbSet) · `ICredentialResolver` (decrypts one credential for one service, enforces per-service scoping and sign-in pass-through rules) · `IConnectivity` (`OpenTcp`, `OpenSsh`, `RunScript`, `ForwardPort`, all jump-aware) · `ITrustStore` (`DecideAsync` for keys and certs) · `ISessionHost` · `IAuditLog` · `ISettingsStore<T>` · `IJobRunner` · `IDiagnostics` · `TimeProvider`.

### 4.4 Guardrails built into the SDK

- Plugin routes live in a core-created route group, so **authentication, antiforgery, account-status, instance-lock and problem-details are applied by construction**, and a plugin cannot forget them.
- Plugins get credentials only through `ICredentialResolver`, which checks host visibility, service scoping and audit in one place.
- Startup validates the contribution graph and fails fast on: duplicate IDs, a missing dependency, a menu action that opens an unknown session type, an audit action used but not declared, or a settings schema that doesn't match.
- A **contract test** (one shared test) runs every plugin's manifest through those checks in CI.
- **Host authorization is structural, not a convention.** The frontend menu is never an authorization mechanism. A host-scoped plugin endpoint declares the permission it needs (`Use` or `Manage`, later a capability). Core loads the host through `HostAccess` and passes the endpoint an authorized `HostContext`. Plugins have no other way to get a host or its credentials. A host the user can't see answers 404, exactly as in 1.x.
- **An authorization sweep test** enumerates every mapped plugin endpoint (REST and WebSocket) and calls it unauthenticated, as an authenticated user with no grants, as another user holding a valid session ID that isn't theirs, as a `Use`-only user on `Manage` endpoints, without the CSRF token, and (for WebSockets) from a foreign `Origin`. It expects refusal every time. New endpoints are covered automatically.
- **Web-proxy origins** (`nkw-*`) get their own tests: no NKeeper cookies or API reachable from them, and framing headers rewritten, ported from 1.x `WebSessionTests`.
- **Secrets are write-only by type.** A settings schema marks fields as secret. They are stored encrypted, never serialized in responses (the API returns `hasValue: true`), and updates use three states: *keep* (field omitted), *replace*, *clear*. This is the 1.x credential behaviour, generalized.

### 4.5 Kinds of dependency

| Kind | Meaning | Example |
| --- | --- | --- |
| **Infrastructure** | core capability, always present | `IConnectivity` (SSH/TCP, jump hosts), pinned `HttpClient`, `ITrustStore`, `ICredentialResolver` |
| **Hard plugin dependency** (`dependsOn`) | can't work without it; startup refuses to enable otherwise | `db-mssql` → `db-core`; `kvm` → `virt-core`; `migration` → `storage` (disk tracing) |
| **Capability provider / consumer** | a plugin implements an interface that another plugin's registry consumes. Providers are optional: zero, one or many may be enabled. | `kvm` and `proxmox` register `IGuestProvider` with `virt-core`; `migration` asks the registry for movers and never names a hypervisor. `web` provides `IWebSessionLauncher`, which `idrac` (HTML5 console) and `proxmox` (node web UI) use when present and hide those actions when not. |

Proxmox's API and iDRAC's Redfish use the core pinned `HttpClient`, so they are infrastructure, not dependencies on `web`. The only link to `web` is opening a browser session through its proxy, and that link is an optional capability. Adding a hypervisor means a new `IGuestProvider` plugin, with no change to `virt-core` or `migration`.

---

## 5. Data model changes

| 1.x | 2.0 |
| --- | --- |
| `HostService.Protocol` (`ServiceProtocol` enum, already stored as a string) | `HostService.Type` (string, FK-free, validated against registered types; unknown types are kept and shown as unavailable, just as 1.x keeps `Sftp`/`WinRm`) |
| `HostService.RdpSecurity`, `.Address`, `.Instance` | `HostService.Settings jsonb`, validated by the type's schema. `Address` may stay a core column, since health checks, trust keys and jump logic all need "where does this listen". |
| `HostService.Tools` (`List<string>?`) | unchanged (`text[]`), values validated against the type's registered tools |
| `Host.HypervisorId`, `VirtualMachineId`, `HypervisorSeenAtUtc`, `IsVirtualMachine` | core: `RunsOnHostId`, `IsVirtualMachine` (tag inheritance is core and uses the relationship; inherited tags are for display and grouping only, since access grants match a host's **own** tags, as in 1.x, so changing `RunsOnHostId` never changes access); `ExternalIds jsonb` keyed by provider (`{"proxmox": "cluster/101"}`) instead of a prefixed string parsed by regex |
| `Host.ZabbixHostId` | Zabbix plugin table `zabbix.host_links` |
| `Host.CpuCores`, `MemoryMiB`, `SerialNumber`, `MacAddress` | stay core: several plugins fill them (Linux scan, WMI, iDRAC, Zabbix, hypervisors) |
| `ZabbixSettings`, `EmailSettings`, `InventoryLimits` tables | `PluginSettings(pluginId, data jsonb, protectedSecrets bytea)` via `ISettingsStore<T>`; core settings (limits, email for 2FA) use the same store |
| `ServiceCheck.Protocol` | `ServiceCheck.ServiceType` (string) |
| `HostTrustedKey.Protocol` | `HostTrustedKey.Scope` (string: `"ssh"`, `"rdp"`, `"https"`, `"idrac"`…) |
| One `NKeeperDbContext` | `CoreDbContext` (schema `core`) + one DbContext per plugin that needs tables (own schema, own `__EFMigrationsHistory`). Startup migrates core first, then plugins in dependency order. Disabling a plugin never drops its data. |

**Versioned plugin data.** Every piece of plugin-owned JSON is stored with its schema version: `HostService.Settings`, plugin settings, host extension data and transfer-bundle sections. Each is an envelope like `{ "v": 1, "data": { … } }`, or a sibling `SettingsVersion` column where it needs indexing. The owning plugin provides `Upgrade(fromVersion, json)`, a pure, deterministic function, plus validation. **Reads never write.**
- **On read**, core upgrades old data in memory only, so a request always sees the current shape.
- **Persisting** happens in two places only:
  - a normal save, which goes through the existing optimistic concurrency (`xmin`) and fails cleanly on a conflict;
  - a **startup upgrade step** after each plugin's migrations. It rewrites outdated rows in batches, each batch in a transaction with `WHERE v = <old>` guards, so it is idempotent and safe to repeat.
- A plugin can't be enabled while its upgrade step is failing. Plugin tables are versioned by their own EF migrations. Bundles record each plugin's data version, so an import can upgrade older sections.

**Installed vs enabled.** States are defined in [plugin-security.md §8](plugin-security.md#8-installed-enabled-pinned):
- *Installed:* a verified package is in the plugin directory, but its code isn't loaded.
- *Enabled:* an administrator turned it on. Its code loads at startup, and its migrations run.
- *Disabled* or *removed:* its data and settings are kept, and its services stay in the inventory as unavailable.

The same applies to service types no installed plugin provides.

---

## 6. Proposed repository structure

Vertical slices: **a plugin is one folder** with its backend, frontend, tests and docs together.

```
NKeeper_2/
├─ README.md
├─ docs/
│  ├─ v2-plan.md                     ← this document
│  ├─ plugin-security.md             plugin packages, signing, verification and loading
│  ├─ architecture.md                core/plugin model, extension points (written in phase 0)
│  ├─ plugin-authoring.md            how to write a plugin, with the ssh plugin as the worked example
│  └─ (installation, user-guide, administration, configuration, security, troubleshooting — ported from 1.x)
│
├─ core/
│  ├─ backend/
│  │  ├─ NKeeper.Abstractions/       plugin SDK: contracts, descriptor, contribution types (no EF)
│  │  ├─ NKeeper.Core/               kernel services: inventory, access, vault, audit, sessions,
│  │  │                              trust store, pinned HttpClient, health scheduler, jobs,
│  │  │                              settings, transfer
│  │  ├─ NKeeper.Plugins.Host/       package verification (shared with the packaging tool),
│  │  │                              filesystem checks, load contexts, pinning, enable state
│  │  ├─ NKeeper.Connectivity.Ssh/   IConnectivity over SSH.NET: connections, scripts, port forwards,
│  │  │                              jump tunnels (infrastructure: no UI, no service type)
│  │  ├─ NKeeper.Core.Persistence/   CoreDbContext, entity configs, migrations, plugin DbContext base
│  │  ├─ NKeeper.Identity/           Identity, auth session store, auth providers (local, LDAP, OIDC, email 2FA)
│  │  └─ NKeeper.Server/             Program.cs only: composes core, runs the plugin host, middleware, SPA hosting
│  └─ frontend/
│     ├─ app/                        shell: router, layout, workspace (tree, tabs, dialogs), keyboard
│     ├─ ui/                         component kit (Button, Dialog, ContextMenu, ResultGrid, Splitter…)
│     └─ sdk/                        @nkeeper/sdk: definePlugin, registries, api client, session hooks,
│                                    generic menu builder, credential prompt, SessionChrome
│
├─ plugins/
│  ├─ ssh/                           SSH service type, terminal session, recordings
│  │  ├─ backend/NKeeper.Plugins.Ssh.csproj
│  │  ├─ frontend/ (package @nkeeper/plugin-ssh)
│  │  ├─ tests/NKeeper.Plugins.Ssh.Tests.csproj  (+ frontend *.test.tsx)
│  │  └─ README.md
│  ├─ telnet/
│  ├─ desktop/                       RDP + VNC via guacd (requires: guacd), vendored Guacamole client
│  ├─ web/                           HTTP/HTTPS service types, YARP proxy on nkw-* origins
│  ├─ linux/                         tools on SSH: Files (SFTP), Journal, SELinux; Linux internal scanner
│  ├─ storage/                       iSCSI / multipath / DRBD tool; StorageTrace (exposed to dependants)
│  ├─ windows/                       RPC service type, RPC session, Event Log Viewer, WMI scanner (requires: impacket on Linux)
│  ├─ virt-core/                     guest model, IGuestProvider registry, console channel relay (SPICE/VNC), guest menu & tasks, VM capacity report
│  ├─ kvm/                           libvirt over SSH: VM list, power, consoles (depends: virt-core; provides IGuestProvider)
│  ├─ proxmox/                       Proxmox service type, API client, consoles, tasks, cluster scan (depends: virt-core; provides IGuestProvider; uses IWebSessionLauncher if enabled)
│  ├─ migration/                     planner, movers, jobs (depends: virt-core, storage; movers come from whichever guest providers are enabled)
│  ├─ idrac/                         iDRAC service type, Redfish, RACADM derivation to SSH (uses IWebSessionLauncher for the HTML5 console if enabled)
│  ├─ db-core/                       database session store, query window, explorer, blocking, report framework, IDatabaseEngine
│  ├─ db-mssql/                      SQL Server engine, Browser lookup, TDS cert probe, orphans, instance scanner
│  ├─ db-oracle/                     Oracle engine, SQL*Plus splitter, DBA reports, service-name scanner
│  ├─ db-ase/                        SAP ASE + Replication Server engines, reports, Copy Database, drift compare
│  ├─ zabbix/                        settings, host link, detail section, comparison report, import
│  ├─ import-mremoteng/              mRemoteNG importer
│  ├─ network-scan/                  external port scan, ARP neighbour proposals
│  ├─ wake-on-lan/                   (tiny: shows how small a plugin can be)
│  └─ reports-ip/                    IP address map report
│
├─ testing/
│  ├─ NKeeper.Testing/               ApiFactory, Postgres fixture, SSH/xrdp/guacd fixtures, ManualClock,
│  │                                 fake-CLI script harness, plugin contract test
│  └─ fixtures/                      shared fake scripts (virsh, multipath, journalctl…) and test-container
│                                    Dockerfiles (development and CI only, never deployment)
│
├─ tools/
│  └─ NKeeper.PluginTool/            keygen, pack, sign, verify (.nkp packages)
│
├─ deploy/                           native Linux only (D8)
│  ├─ systemd/                       hardened nkeeper.service (ProtectSystem=strict, non-root user)
│  ├─ nginx/
│  └─ setup.sh                       install / upgrade / uninstall on Debian/Ubuntu and RHEL-family
│
├─ NKeeper.slnx                      all backend projects
├─ package.json                      npm workspaces: core/frontend/*, plugins/*/frontend
└─ Directory.Build.props             .NET 10, nullable, warnings-as-errors (from 1.x)
```

### 6.1 How plugins are built, packaged and loaded

- **Build:** each plugin builds as its own backend assembly and frontend ES-module bundle. `NKeeper.Server` does **not** reference any plugin project, and the architecture tests enforce that. React and `@nkeeper/sdk` are externals, provided by the shell's import map.
- **Package:** `NKeeper.PluginTool` packs each plugin into a signed `.nkp`. Built-in plugins are packed and signed by the build: with a development key locally and in CI, and with the official key for releases (D12).
- **Load:** at startup the plugin host verifies every package in the plugin directory and loads the enabled ones from their verified in-memory bytes, each into its own `AssemblyLoadContext`. The frontend shell asks `GET /api/plugins` which plugins are enabled and imports their entry modules from `/plugins/<id>/…`. Heavy views stay lazy-loaded inside each bundle. Changes take effect at the next restart.
- **Built-in plugins use the same path.** SSH, desktop and web ship as packages in the release and are verified like any other plugin, so the loader is exercised on every start.
- **Untrusted third-party plugins** (Phase 8) would run out of process, since `AssemblyLoadContext` isolates versions, not privileges. Signed, trusted plugins run in process as described in [plugin-security.md](plugin-security.md).

### 6.2 What "add a service type" looks like in 2.0

1. `plugins/foo/backend`: register `ServiceType("foo", defaultPort: 1234, settings: FooSettings)`, a `SessionType`, a `HealthProbe` (or reuse TCP), endpoints.
2. `plugins/foo/frontend`: `definePlugin({ tabs: { foo: lazy(() => import('./FooTab')) }, serviceEditors: { foo: FooFields } })`.
3. Menu: one declarative `HostAction { location: "connect", appliesTo: { serviceType: "foo" }, credentials: "saved|prompt", opens: "foo" }`.
4. Tests in `plugins/foo/tests`.

Nothing in `core/` changes. In 1.x the same change touches about 12 files across both tiers.

---

## 7. Key designs in more detail

### 7.1 Session framework (core)

Two kinds, one lifecycle:

- **Stream sessions** (WebSocket at `/api/sessions/{type}`): the 1.x control phase becomes core: `start` → resolve target → `hostKey` trust prompt → `connected` → plugin-owned data phase. Core handles the start message, target resolution, TOFU prompt, error codes, keep-alive, limits, recording hooks and audit (`session.open`). The plugin implements `IStreamSession.RunAsync(target, socket)`. Users: ssh, telnet, rdp/vnc, rpc, journal follow, console channels.
- **Stateful sessions** (REST plus a keep-alive store): core's `SessionStore<T>` (owner, idle expiry, per-user cap, `pagehide` DELETE, "end all for user"). Users: web, database, VM consoles, SFTP, iDRAC.

**Ongoing session authorization.** Checking access only when a session opens isn't enough, because NKeeper is the gateway to privileged infrastructure.
- **Every session is registered in one core `SessionRegistry`**, whatever its kind or plugin. It records the owner, host, service, the permission it required, the sign-in session that opened it, and its type. "End all for user" becomes one call. 1.x's `EndSessionsAsync` names four stores by hand, and that couples core to every session plugin.
- **Session IDs are bearer-proof.** Each stateful operation and each WebSocket channel attach checks that the caller is the owner *and* is signed in under a live sign-in session. Someone else's ID gets the same 404 as an unknown one. IDs are random 128-bit values, never sequential.
- **Access changes end sessions that are no longer allowed.** Core raises an `AccessChanged` event for:
  - grants replaced;
  - a host's own tags edited (tag-based grants match own tags only, never inherited ones, so a `RunsOnHostId` change is not an access change);
  - a host deleted, or its service or credential removed;
  - a user disabled, deleted, or changed role;
  - the user's sign-in session revoked or expired.
  
  The registry re-evaluates only the affected (user, host) pairs and terminates sessions that are no longer authorized. The browser gets a `closed` message ("access revoked"), and the audit gets `session.close` with reason `access_revoked`. **This fixes a 1.x gap:** 1.x ends sessions when a user is disabled or deleted, but not when their grants or a host's tags change.
- **Stateful operations re-check cheaply.** Each operation compares the session's stored access stamp with a per-user access version that core bumps on any `AccessChanged`. They match on the hot path, and only on a mismatch does it re-evaluate against `HostAccess`.
- REST and WebSocket sessions share this registry and these rules. Tests cover revoking during an open SSH terminal, during a database query, and during a web-proxy session.

The 1.x error-code catalogue (`SessionErrorCodes`) splits into core codes plus plugin-declared codes.

### 7.2 Menus (frontend SDK)

The generic builder knows the rules that are core concepts today: credential scoping (`usedWith`), prompts, sign-in pass-through, Telnet-style "no login", jump-host incompatibility (from `SessionType.supportsJumpHost`), and grouping (`Connect`, `Connect ▸ Tools`, `Tasks`, VM submenus). Plugins contribute data, not menu code. For the irregular cases (a VM's console through its hypervisor's service and login) there's an escape hatch: `resolve(host, ctx) → MenuEntry[]`, provided by `virt-core`.

### 7.3 Scanning

Core runs a scan as a pipeline: pick scanners that apply to the host (by service type and kind), run them over one shared connection where possible (the single-SSH-login property of 1.x is kept by letting Linux-side scanners contribute *script sections* to one combined script), merge `ScanContribution`s, and run the proposal engine (new hosts from neighbours, guest links, cluster nodes, unlink). The matchers (`GuestMatcher`, `ClusterNodeMatcher`, `NeighborMatcher`) are pure and generic, so they move to core inventory.

### 7.4 Instance transfer

Moving a 2.0 instance between servers works as in 1.x: one passphrase-encrypted bundle, with secrets re-encrypted for the new server.
- **Core** owns the generic parts:
  - `BundleReader`/`BundleWriter`;
  - an `ImportCoordinator` (preview, ordering by plugin dependency, one transaction, ID remapping, secret re-encryption);
  - the core sections: users, grants, hosts, tags, credentials, trusted keys, hardware and `RunsOnHostId`.
- **Each plugin** adds its own section through a `TransferParticipant`, versioned like all plugin data (§5).
- A section for a plugin that isn't installed on the target is kept in the preview and reported, never silently dropped.

**Upgrading from 1.x is deferred** (D10). If it's added later, it fits the same split without touching core: an `import-v1` plugin reads the 1.x bundle and maps the core parts. Each plugin claims its own 1.x protocol names and fields through the import aliases on its `ServiceType`. The data model table in §5 and Appendix A record where every 1.x field would go.

### 7.5 Permissions (optional extension, flagged)

1.x has Use/Manage per host. Plugins could declare **capabilities** (`storage.action`, `mssql.kill`, `vm.power`, `migration.run`) that grants could include, finer-grained than "Use". Decided (D6): build the hook in the SDK now (capabilities declared, all granted with Use or Manage as today) and decide later whether to expose it in the UI.

### 7.6 Runtime plugins (summary of [plugin-security.md](plugin-security.md))

- **Packages:** a plugin ships as a signed `.nkp` ZIP. Its signed `files.json` lists every file with its SHA-256, and ECDSA P-256 is used, built into .NET.
- **Installation is root-only:** packages live in `/usr/lib/nkeeper/plugins/` and trusted keys in `/etc/nkeeper/plugin-trust/`, all owned by root and not writable by the service. The server checks ownership and modes at startup, refuses to run as root, and runs under a hardened systemd unit (`ProtectSystem=strict`). There is no upload through the web UI or the API.
- **Verification fails closed,** in a fixed order, each step with a reason code:
  - the ZIP structure and hard size limits, including the signature metadata, before anything is decompressed;
  - signature by a trusted, unrevoked key that is allowed to sign that ID (unprefixed IDs are reserved for the official key; development keys only in `Development`);
  - the exact file set, and every file's hash;
  - SDK compatibility, no bundled shared assemblies;
  - pinning (same version with a new hash is refused, and downgrades are refused);
  - dependencies.
- **A trust directory that fails its checks refuses every plugin**, since ignoring it would also ignore local revocations.
- **The CSP stays strict.** The shell's import map is the one inline script, allowed by its hash.
- **Loading:** the code loads from the same in-memory bytes that were verified, into a per-plugin `AssemblyLoadContext`. The frontend files are served from that same verified copy. Changes apply at restart.
- **Who does what:** root installs (`plugin install`), and NKeeper administrators enable or disable. Every state change, and every refusal, is audited. A refusal also raises an alert.

---

## 8. Gameplan

Each phase ends in a state that builds, passes tests, **installs as a native Linux service**, and does something useful. Docker is used only for the test containers in development and CI (D8). Exit criteria are checkable. The order proves the SDK with real plugins *before* most of the core is built around it.

### Phase 0: Architecture skeleton
- **Security first:** the plugin host and the package tooling come before anything else, built to [plugin-security.md](plugin-security.md):
  - `NKeeper.Plugins.Host`: package verification, filesystem checks, pinning, load contexts, enable state;
  - `NKeeper.PluginTool`: keygen, pack, sign, verify;
  - a test for every refusal path (§11 there).
- Solution, npm workspaces, `Directory.Build.props`, lint/format, and CI: build, tests including the Testcontainers ones, lint, type-check, and package verification of the built-in plugins. CodeQL and Dependabot for NuGet and npm.
- `NKeeper.Abstractions` v0 (only the contracts in §4.2's v0 table), dependency validation, installed/enabled state.
- `@nkeeper/sdk` v0: `definePlugin`, registries, the import map for shared modules, the API client (port `lib/api.ts`: CSRF, problem details).
- Architecture tests:
  - plugins reference only `NKeeper.Abstractions`;
  - `NKeeper.Server` references no plugin;
  - core never mentions a service type ID.
- **Exit:**
  - the server boots, verifies a signed no-op plugin package, loads it and serves `GET /api/plugins`;
  - the shell loads the plugin's frontend module and lists it;
  - a tampered copy of the package is refused with the right reason code;
  - CI runs all of it.

### Phase 1: Minimal core + the SSH plugin
- Only the core that an SSH session needs: local sign-in, bootstrap admin, users, Use/Manage grants, `HostAccess` and `HostContext`, inventory (hosts, tags, services with versioned settings, credentials with per-service scoping), vault and key ring, the session framework (stream kind), trust store, `NKeeper.Connectivity.Ssh` with jump hosts, and audit events.
- The **`ssh` plugin**: service type, terminal session, TOFU prompt, `Connect` menu action, TCP health probe.
- Frontend shell: sign-in, tree with search and group-by, a host editor driven by service-type schemas, tabs.
- A first native install: a hardened systemd unit, nginx, PostgreSQL, and the root-owned plugin and trust directories (an early version of the Phase 6 installer).
- The authorization sweep test (§4.4) goes in now, while there are few endpoints.
- Port the 1.x tests for auth, access, inventory, jump hosts, key ring and SSH sessions.
- **Exit:** a deployable build in which you can sign in, add hosts and open SSH terminals (also through a jump host). This is usable for daily SSH work.

### Phase 2: Prove different plugin shapes
- **web**: stateful session kind, separate `nkw-*` origins and middleware, a service type without credentials, plugin settings (base domain), `IWebSessionLauncher` capability.
- **wake-on-lan**: an action-only plugin, the minimum size.
- **desktop, proof only:** RDP connects through guacd with a pinned certificate and a saved credential, **with the browser instruction allow-list (1.x `GuacamoleInstructionFilter`) and its negative tests from the start**, because it is a security control on a deployable relay, not a parity feature. That exercises the external-daemon requirement, the certificate probe in the trust store, and the credential path. AD sign-in pass-through, VNC and clipboard follow in Phase 4, so desktop parity doesn't hold up the SDK freeze.
- **guacd as a native service (D11):** build guacd 1.6 from source on each supported distribution family in CI, and run the RDP proof against that build. If a family can't build or run it reliably, that family falls back to guacd in a container, and the plan records the result.
- Health scheduler and TLS probes; the health report.
- **Exit:** SSH and web parity with 1.x, an RDP session opens end to end, and the ported 1.x tests for those paths pass. wake-on-lan touched zero core files.

### Phase 3: Freeze SDK v1
- Review what hurt in Phases 1–2. Remove abstractions nothing used, fix the ones that leaked, and rename freely while there are only four plugins.
- Write `docs/plugin-authoring.md` with ssh and wake-on-lan as worked examples. Freeze `sdkVersion` 1. From here, SDK changes are additive or versioned.
- **Exit:** SDK v1 documented, with contract and architecture tests green.

### Phase 4: Port the remaining features
Each extension point is introduced with its first user (§4.2), and each plugin ports its 1.x tests. Suggested order, cheapest proofs first:
1. `desktop` parity (VNC, AD pass-through, clipboard), `telnet`, `linux` (Files, Journal, SELinux, plus **Scanner** and **ServiceTool**: the scan pipeline is built here), `storage`.
2. `windows` (RPC, Event Log, WMI scanner), `idrac` (endpoint derivation for RACADM, optional `IWebSessionLauncher`).
3. `db-core` (**sub-registry** pattern), then `db-mssql`, `db-oracle`, `db-ase`.
4. `virt-core` (`IGuestProvider` registry, console relay), then `kvm`, `proxmox`, then `migration` (jobs).
5. `zabbix` (**HostDataExtension**, **Report**), `network-scan`, `reports-ip`.
6. Identity providers: AD (LDAP), OIDC, email 2FA (**AuthProvider**).
- **Exit:** feature parity with 1.x, every step a deployable build.

### Phase 5: Instance transfer
- **TransferParticipant**s and 2.0 export/import (§7.4). There is no 1.x import (D10).
- **Exit:** an instance exports from one 2.0 server and imports into another with every host, credential, trusted key, grant and plugin section intact.

### Phase 6: Deployment and docs
- **The native Linux installer** (`deploy/setup.sh`) for Debian/Ubuntu and RHEL-family:
  - install, upgrade and uninstall;
  - the hardened systemd unit, nginx with HTTPS, PostgreSQL;
  - the plugin and trust directories with the right ownership;
  - guacd as decided in D11.
- The plugins admin page: installed, enabled and refused, with signer, hash and requirements.
- The `plugin` command line.
- Port and update the user, admin and security docs; write a README for each plugin.
- **Exit:** fresh install, upgrade and plugin install documented and tested end to end on both distribution families.

### Phase 7 (optional, post-2.0): Capability permissions in the UI
Expose plugin-declared capabilities (`storage.action`, `mssql.kill`, `vm.power`, `migration.run`) in grants.

### Phase 8 (optional, post-2.0): Untrusted third-party plugins
`AssemblyLoadContext` is **not** a security boundary: a plugin loaded in process has the API's full privileges, including the vault. Signed plugins from trusted keys load that way from Phase 0 on ([plugin-security.md](plugin-security.md)). **Untrusted third-party plugins would run out of process** (a separate worker speaking a narrow RPC protocol, given only the capabilities and credentials core chooses to pass it), with a remote ES module for their frontend.

### Porting rules (all phases)
- **Port, don't rewrite, the domain logic.** Parsers, planners, dialects, scripts and their tests move nearly verbatim into their plugin. The refactor is in the seams, not the SQL or shell scripts.
- **Port the tests alongside the code**, and keep the real-server fixture approach.
- A plugin may depend only on `NKeeper.Abstractions` and on plugins listed in its manifest. Enforce this with project references (and an architecture test).
- Nothing in `core/` may mention a specific service type ID.

---

## 9. Decisions

Decided on 2026-11-09 unless marked open. Changing one is a design change: update this table first.

| # | Question | Decision | Notes |
| --- | --- | --- | --- |
| D1 | Plugins compiled in, or loaded at run time? | **Loaded at run time from signed packages.** | In-process, so only trusted keys. Root installs and admins enable, with no web upload. Every file is hash-checked and every package signature verified. See [plugin-security.md](plugin-security.md). Untrusted plugins would need Phase 8. |
| D2 | New repository or refactor 1.x in place? | **New repository, clean start**, porting domain logic and tests from 1.x. | 1.x stays available while 2.0 is built. |
| D3 | Keep the stack? | **Yes:** .NET 10, React and Vite, PostgreSQL, guacd. | |
| D4 | Plugin granularity | **One plugin per product area** (e.g. `linux` holds Files, Journal and SELinux). | Fewer packages and dependency edges. A tool that grows its own data or dependants can be split out later without breaking anything, since IDs and service types don't change. |
| D5 | Upgrade path from 1.x | **Deferred with D10.** If built, by bundle import, not in-place migration. | §7.4 records how it would fit without touching core. |
| D6 | Capability-level permissions | **Hook only:** plugins declare capabilities, and grants stay Use/Manage. | The UI is Phase 7. Declaring capabilities from the start avoids re-touching every plugin later. |
| D7 | Scale-out sessions | **No.** Sessions stay in-process, behind `ISessionStore` and the session registry. | A single node is the supported deployment, and the interfaces keep scale-out possible later. |
| D8 | Install modes | **Native Linux only** (systemd, nginx, PostgreSQL). No IIS, no Docker or container deployment. | Docker remains a development and CI dependency for the Testcontainers test suite only, because real-server tests need it. |
| D9 | SSH / RDP / web as core code or bundled plugins? | **Built-in plugins**, loaded and verified like any other. | §3.2. |
| D10 | Migration from 1.x | **Not now.** | No `import-v1` plugin and no 1.x fixture. Can be added later as a plugin. |
| D11 | How is guacd provided without Docker? Distributions don't ship guacd 1.6, which is why 1.x's native mode still ran it in a container. | **Native service first:**<br>- the installer builds guacd 1.6 from source against the distribution's FreeRDP and libvncserver;<br>- it runs as its own unprivileged systemd service, listening on `127.0.0.1` only.<br>**If that doesn't work** reliably on a supported distribution, guacd alone runs in a container (Podman), as the one "absolutely required" exception to D8. | Proven in Phase 2: a build on each supported distribution family in CI, then an RDP session end to end. The fallback is decided per distribution family, from those results. |
| D12 | How is the official plugin signing key held and rotated? | **Two copies of one dedicated release key:**<br>- one as a secret of a protected GitHub Actions environment, which requires maintainer approval for each release;<br>- one offline backup held by the maintainer.<br>Each release embeds the current public key and the next one, for rotation. | The maintainer generates the key locally with `NKeeper.PluginTool keygen --release`. It is never generated by automation and never committed. Until the first release, all builds use development keys, which production servers refuse. |


---

## Appendix A: 1.x → 2.0 location map

| 1.x (backend `src/NKeeper.Api/…`, frontend `src/features/…`) | 2.0 |
| --- | --- |
| `Access/`, `Users/`, `Security/`, `Infrastructure/Identity` | `core/backend/NKeeper.Core` (access), `NKeeper.Identity` |
| `Inventory/HostManager`, `Contracts/Inventory`, `Infrastructure/Inventory` | `NKeeper.Core` inventory |
| `Inventory/Scanning/{Guest,ClusterNode,Neighbor}Matcher`, `NeighborTable` | `NKeeper.Core` scan pipeline + proposals |
| `Inventory/Scanning/InternalScan` | `plugins/linux` (scanner) |
| `Inventory/Scanning/ExternalScan`, `ScanPorts` | `plugins/network-scan` |
| `Inventory/Scanning/{ImpacketWmiScan,WindowsInternalScan,WmiMapping,ImpacketHelper}`, `wmi_scan.py` | `plugins/windows` |
| `Inventory/Scanning/SqlServerInstances`, `OracleDatabases` | `plugins/db-mssql`, `plugins/db-oracle` (scanners) |
| `Inventory/Import/` | `plugins/import-mremoteng` |
| `RemoteSessions/` (protocol, resolver, limiter, socket, trusted keys, origin) | `NKeeper.Core` session framework + trust store |
| `RemoteSessions/JumpTunnel`, `Ssh/SshCommandConnection`, `Ssh/SshScript`, `Ssh/SshAuthentication` | `NKeeper.Core` connectivity |
| `Ssh/SshTerminalSession`, `Auditing/TerminalRecorder` / `terminal/SshTerminal` | `plugins/ssh` (recording storage stays core audit) |
| `Telnet/` | `plugins/telnet` |
| `Desktop/` / `DesktopSession`, vendored guacamole | `plugins/desktop` |
| `Web/` / `WebSession` | `plugins/web` (`CertificateTrust` → core trust store) |
| `SftpController` / `FileBrowser` | `plugins/linux` (Files) |
| `Journal/`, `Selinux/` + controllers / views | `plugins/linux` |
| `Storage/` / `StorageView` | `plugins/storage` |
| `Rpc/`, `EventLogs/` / `RpcSession`, `EventLogView` | `plugins/windows` |
| `Kvm/ConsoleEndpoints`, `ConsoleSessionStore` / `SpiceConsole`, `VncConsole`, vendored spice | `plugins/virt-core` |
| `Kvm/KvmHost` / `VirtualMachinesView`, `KvmVmConsole` | `plugins/kvm` |
| `Kvm/ProxmoxHost`, `Proxmox/` + controller | `plugins/proxmox` |
| `Migration/` / `MigrateDialog`, `GuestTaskDialog` | `plugins/migration` (guest tasks → `virt-core`) |
| `Idrac/` / `IdracView` | `plugins/idrac` |
| `Databases/` / `features/databases` (generic parts) | `plugins/db-core` |
| `SqlServer/`, `Oracle/`, `Ase/` | `plugins/db-mssql`, `db-oracle`, `db-ase` |
| `Zabbix/`, `Infrastructure/Zabbix` / `features/zabbix` | `plugins/zabbix` |
| `Health/HostHealth`, `HealthMonitor` / `HealthReport` | `NKeeper.Core` health scheduler + per-type probes |
| `Health/WakeOnLan` | `plugins/wake-on-lan` |
| `Auditing/` / `features/audit` | `NKeeper.Core` audit |
| `Diagnostics/` / `DiagnosticsPanel` | `NKeeper.Core` diagnostics |
| `Transfer/` / `features/transfer` | `NKeeper.Core` transfer (generic bundle + coordinator) + `TransferParticipant`s (1.x import deferred, D10) |
| `features/reports` (IP, VM resources, inventory) | `plugins/reports-ip`, `plugins/virt-core`, core |
| `features/appearance`, `lib/hotkeys` | `core/frontend/app` |
| `scripts/`, `container/`, `frontend/nginx`, compose | `deploy/`: native Linux only (systemd, nginx, `setup.sh`); the container and IIS modes are dropped (D8) |
