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
3. Migrate the core schema.
4. Plugin host: check the plugin and trust directories, verify every package, check pins and dependencies, and load the enabled plugins from their verified bytes, each into its own `AssemblyLoadContext`.
5. Call each loaded plugin's `ConfigureServices`. If one throws, its registrations are rolled back, and it and the plugins that depend on it are refused (`load_failed`); the others carry on. With `Plugins:RequireAllEnabledLoaded`, any enabled plugin that didn't load stops startup instead.
6. Build the application, then map core endpoints, each plugin's endpoints (`MapEndpoints`), and the shell's fallback route. A plugin that fails in `MapEndpoints` stops startup, because endpoints can't be unmapped.

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
- `endpoints` is a route group core created at `/api/p/<id>/`. As core gains authentication, antiforgery and rate limits (Phase 1), it applies them to the group, so a plugin can't forget them ([v2-plan.md §4.4](v2-plan.md#44-guardrails-built-into-the-sdk)).
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

## 6. Where to read more

- [v2-plan.md](v2-plan.md): the audit of 1.x, the core/plugin boundary, the contribution catalogue, phases and decisions.
- [plugin-security.md](plugin-security.md): package format, signing, trust, directory checks, verification order and reason codes, loading and states.
- [CONTRIBUTING.md](../CONTRIBUTING.md): development setup and running the shell with plugins.
- `plugin-authoring.md` (Phase 1): writing a plugin, with SSH as the worked example.
