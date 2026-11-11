# Writing an NKeeper plugin

This guide is for developers writing a plugin against **SDK v1** (`NKeeper.Abstractions` 1.0.0 and `@nkeeper/sdk` 1.0.0). It walks through two real plugins from this repository:
- `wake-on-lan`, the smallest shape: a service type, a run action and one endpoint;
- `ssh`, a session plugin: a terminal that core connects, with a frontend view.

The design behind every contract is in [architecture.md](architecture.md). Signing and loading are in [plugin-security.md](plugin-security.md). Section numbers below (§7.4 and so on) point into architecture.md.

## 1. What a plugin is

A plugin is a signed package (`.nkp`) holding:
- **a backend**: a .NET 10 assembly with one class implementing `IPlugin`, plus any NuGet libraries it brings;
- **a frontend**, optionally: one ES module that the shell imports;
- **a manifest**, `nkeeper-plugin.json`: its ID, version, the SDK range it supports, and where its entry points are.

It references **only the SDK**: `NKeeper.Abstractions` in the backend, `@nkeeper/sdk` in the frontend. Never core's own assemblies or modules; an architecture test enforces this for the built-in plugins.

### What core does for you, and what a plugin can't do

Core keeps the security-sensitive parts, so every plugin gets them the same way and none can skip them:
- **Authorization.** Your endpoints live under `/api/p/<id>/`, behind core's sign-in, antiforgery and rate limits (§4). In a host group, core checks the user's access to the host before your code runs (§7.3).
- **Credentials.** A plugin never sees a password or key (D13). It gets an opaque `CredentialHandle`, and core opens the connection with it (§7.6, §7.9).
- **Server trust.** Core checks every SSH host key and TLS certificate against the trust store, and asks the user about new ones (§7.7). A plugin can't trust a key.
- **Sessions.** Core runs the session protocol, limits, prompts and audit (§7.10); the plugin carries the data.
- **Audit.** Core records sessions, credential use and trust itself. A plugin records its own operations, under its own ID (§7.12.1).

## 2. Layout and build

A plugin in this repository:

```text
plugins/<id>/
  backend/
    NKeeper.Plugins.<Name>.csproj   imports plugins/NKeeperPlugin.targets
    nkeeper-plugin.json             the manifest
    <Name>Plugin.cs                 the IPlugin
  frontend/                         optional
    package.json                    @nkeeper/plugin-<id>, depends on @nkeeper/sdk ^1.0.0
    vite.config.ts                  builds dist/index.js, keeping the shared modules external
    src/index.tsx                   default-exports definePlugin(...)
```

The backend project is a few lines. With a frontend, it names the frontend's npm package:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <NKeeperPluginFrontendPackage>@nkeeper/plugin-ssh</NKeeperPluginFrontendPackage>
  </PropertyGroup>
  <Import Project="../../NKeeperPlugin.targets" />
</Project>
```

`NKeeperPlugin.targets` references the SDK without copying it, builds the frontend with npm, then packs and signs the plugin into `artifacts/plugins/`. In development it signs with a key it creates in `.dev/keys/`. To load your plugin, point `Plugins:Directory` at `artifacts/plugins` and `Plugins:DevelopmentTrustDirectory` at `.dev/keys` (see [CONTRIBUTING.md](../CONTRIBUTING.md)).

The manifest:

```json
{
  "formatVersion": 1,
  "id": "wake-on-lan",
  "name": "Wake-on-LAN",
  "version": "0.1.0",
  "sdkVersion": ">=1.0.0 <2.0.0",
  "backend": {
    "entryAssembly": "backend/NKeeper.Plugins.WakeOnLan.dll",
    "entryType": "NKeeper.Plugins.WakeOnLan.WakeOnLanPlugin"
  }
}
```

A plugin with a frontend adds `"frontend": { "entry": "frontend/index.js" }`. The ID is lower-case letters, digits and `-`; plugin-security.md §5 has every rule the server checks. Built-in plugins also say `"builtIn": true`.

## 3. The backend contract

```csharp
public interface IPlugin
{
    void Contribute(IPluginContributions contributions, PluginContext context) { }   // what you add to core's catalogues
    void ConfigureServices(IServiceCollection services, PluginContext context);      // your own services
    void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context);       // your endpoints, under /api/p/<id>/
}
```

- `PluginContext` has your ID and version, from the verified manifest, and your configuration section, `Plugins:Settings:<id>`.
- **Contributions** are data that core validates at startup (§7.4): service types, session types and host actions. A malformed one stops the plugin from loading with `contribution_invalid`, so mistakes show at once.
- IDs you contribute are namespaced by convention: a service type or session type is usually the plugin's ID (`ssh`); a host action is the plugin's ID, a dot, and a name (`ssh.terminal`).

## 4. Worked example: wake-on-lan

Wake-on-LAN wakes a host with a magic packet. It shows the minimum: one service type with a setting, a run action, one endpoint and one audit event (§7.15). Source: `plugins/wake-on-lan/backend/WakeOnLanPlugin.cs`.

### The service type

```csharp
contributions.AddServiceType(new ServiceType("wake-on-lan", "Wake-on-LAN")
{
    DefaultPort = 9,
    Settings = [ServiceSetting.Text("macAddress", "MAC address", maxLength: 17, required: true)],
});
```

A **service type** is a kind of service a host can have. Its settings are a schema: the shell draws the host editor's form from it, and core validates what's saved against it (§7.4). Settings can be text, number, switch or choice. A service type can also name a **health probe**, `HealthProbe.Tcp` or `HealthProbe.Tls`, which core runs on its own schedule (§7.13).

### The run action

```csharp
contributions.AddHostAction(new HostAction("wake-on-lan.wake", "Wake")
{
    Endpoint = "wake",
    ServiceTypes = ["wake-on-lan"],
    Credential = HostActionCredential.None,
});
```

A **host action** is an entry in a host's Connect menu (§7.12). This one runs an endpoint rather than opening a session. The shell offers it on each `wake-on-lan` service, and calls `POST /api/p/wake-on-lan/hosts/<hostId>/wake` with `{ serviceId }`. Set `Credential = Required` instead, and the shell adds `credentialId` or `promptReference`, which the endpoint turns into a handle with `ICredentialBroker` (§7.6).

### The endpoint

```csharp
public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context) =>
    endpoints.MapHostGroup(HostPermission.Use).MapPost("/wake", WakeAsync);

private static async Task<IResult> WakeAsync(HttpContext http, WakeRequest? request, IPluginAudit audit)
{
    var host = http.GetHostContext();   // already authorized: the user can use this host
    var service = host.Services.FirstOrDefault(s => s.Id == request?.ServiceId && s.Type == "wake-on-lan");
    if (service is null)
    {
        return Results.Problem(statusCode: 400, detail: "This host has no such Wake-on-LAN service. Reload the host and try again.");
    }

    // … read service.Settings, send the packet …
    await audit.RecordAsync("wake-on-lan.send", host.Id, $"{mac} to {targets}", http.RequestAborted);
    return Results.Ok(new { message = $"Sent a Wake-on-LAN packet to {mac}." });
}
```

- `MapHostGroup(HostPermission.Use)` makes a group at `hosts/{hostId}/`. Core answers `404` before your code runs for a host the user can't see, and `403` on a `Manage` group for a user with Use only (§7.3).
- `HostContext` holds the host and its services, with their settings, and never a secret.
- **Check the service yourself.** The request names a service ID; make sure it belongs to this host and is of your type.
- A run action answers `{ message }`, which the shell shows. A refusal is a problem response; the shell shows its `detail`.
- `IPluginAudit` records under your plugin's ID only: the action must start with `<id>.` (§7.12.1).

That's the whole plugin. It needs no frontend: the shell draws the menu and shows the answer.

## 5. Worked example: ssh

The ssh plugin opens terminals. Core opens the SSH connection, checks the server's key, asks the user about a new one, and signs in with the credential; the plugin only carries the terminal's bytes (§7.11). Source: `plugins/ssh/`.

### Contributions

```csharp
contributions.AddServiceType(new ServiceType("ssh", "SSH") { DefaultPort = 22, Health = HealthProbe.Tcp });
contributions.AddSessionType(new SessionType("ssh", "Terminal", typeof(TerminalSessionHandler)) { ServiceTypes = ["ssh"] });
contributions.AddHostAction(new HostAction("ssh.terminal", "Terminal", SessionType: "ssh"));
```

- A **session type** names a handler that core resolves from your services, so register it: `services.AddScoped<TerminalSessionHandler>()` in `ConfigureServices`.
- A Connect action that names a session type gets an entry per service, with each saved credential and "Type a credential…" (§7.12). The plugin never sees the menu or the credential.
- `SupportsJumpHost` (true by default) says whether the session can go through a host's jump host.

### The stream session

A stream session is one WebSocket that core runs until the plugin's data starts flowing (§7.10). Core has already checked access, resolved the credential and limited the user's sessions when it calls your handler:

```csharp
public sealed class TerminalSessionHandler(IConnectivity connectivity) : IStreamSessionHandler
{
    public async Task<IStreamSession> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken)
    {
        if (request.Credential is null)
        {
            throw new ConnectException(ConnectFailure.NoSecret, "Choose a credential, or type one, to open a terminal.");
        }

        var terminal = new SshTerminal(Columns: Size(request.Options, "columns", 80), Rows: Size(request.Options, "rows", 24));
        var ssh = await connectivity.OpenSshAsync(request.Host, request.ServiceId, request.Credential, cancellationToken);
        try
        {
            return new TerminalSession(ssh, await ssh.OpenShellAsync(terminal, cancellationToken));
        }
        catch
        {
            await ssh.DisposeAsync();
            throw;
        }
    }
}
```

- `request.Options` is whatever your view sent, as JSON. **Treat it as untrusted**: clamp numbers, ignore what you don't expect.
- `IConnectivity` opens connections core checks and authenticates: `OpenSshAsync`, `OpenHttpAsync` and `OpenDesktopAsync` (§7.9, §7.16, §7.20).
- **Let `ConnectException` through.** When a server presents an unknown key, the exception carries it (`ConnectFailure.HostKeyUnknown` with a `PresentedIdentity`). Core catches it, asks the user, trusts it if they say so, and calls your handler again. Other failures become the session's error code.

The session then runs until either side ends it:

```csharp
internal sealed class TerminalSession(ISshConnection ssh, ISshShell shell) : IStreamSession
{
    public async Task RunAsync(IStreamChannel channel, CancellationToken cancellationToken)
    {
        // Pump shell output to channel.SendAsync (binary frames), and channel.ReceiveAsync frames to the shell:
        // binary frames are keystrokes, a text frame {"type":"resize",…} resizes it. Return when either side ends.
    }

    public async ValueTask DisposeAsync()
    {
        await shell.DisposeAsync();
        await ssh.DisposeAsync();
    }
}
```

- `channel.ReceiveAsync` returns null when the browser goes. Your token is cancelled when core ends the session (the user's access changed, say).
- **Return promptly.** A `RunAsync` or `DisposeAsync` still running 10 seconds after cancellation is abandoned and logged (§7.10).

### The frontend

The entry module default-exports a definition:

```tsx
import { adoptStyles, definePlugin } from '@nkeeper/sdk'
import { TerminalView, terminalStyles } from './TerminalView'

export default definePlugin({
  id: 'ssh',
  activate() {
    adoptStyles(terminalStyles)   // the shell's policy blocks <style> elements
  },
  sessionViews: { ssh: TerminalView },
})
```

A **session view** draws a session type's tab. It gets `connect(options, signal)`, which runs core's side of the protocol, including core's own dialogs for a new key and a typed credential, and resolves once the session is open:

```tsx
export function TerminalView({ connect, visible }: SessionViewProps) {
  useEffect(() => {
    const opening = new AbortController()
    connect({ columns: terminal.cols, rows: terminal.rows }, opening.signal).then((opened) => {
      opened.onBinary((data) => terminal.write(data))
      terminal.onData((data) => opened.send(data))
      terminal.onResize(({ cols, rows }) => opened.sendJson({ type: 'resize', columns: cols, rows }))
      void opened.closed.then((reason) => /* show why it ended */)
    }, (error) => /* a SessionError, with core's code and message */)
    return () => {
      opening.abort()   // also withdraws any dialog it was waiting on
      // … close the connection
    }
  }, [connect])
}
```

- **Styles.** The shell's content security policy allows stylesheets only from the server. Use `adoptStyles(css)` for your own, and `mirrorInjectedStyles(element)` for a library that adds `<style>` elements (§7.14).
- **Shared modules.** Keep `react`, `react/jsx-runtime`, `react-dom`, `react-dom/client` and `@nkeeper/sdk` external in your build, so the shell and every plugin share one copy of each (§5). The ssh plugin's `vite.config.ts` is a template.
- A view that opens its sessions through its own endpoints instead (the web plugin does) gets `confirmTrust(prompt)` to ask about a certificate your endpoint reported. Your endpoint can return the exception's `PresentedIdentity` as JSON: it's already a `TrustPrompt`.

## 6. SDK v1 at a glance

| Contract | What it's for | Read more |
| --- | --- | --- |
| `IPlugin`, `PluginContext` | the entry point | §4 |
| `MapHostGroup`, `HostContext`, `HostPermission` | endpoints on one host, authorized by core | §7.3 |
| `ServiceType`, `ServiceSetting` | kinds of service, with a settings schema | §7.4 |
| `HealthProbe` | TCP or TLS checks, run by core | §7.13 |
| `HostAction` | Connect menu entries: open a session, or run an endpoint | §7.12 |
| `ICredentialBroker`, `CredentialHandle` | credentials as handles, in your endpoints | §7.6 |
| `IConnectivity` | SSH, HTTP and desktop connections that core opens and checks | §7.9, §7.16, §7.20 |
| `ConnectException`, `ConnectFailure`, `PresentedIdentity` | why a connection failed, and a server identity to trust | §7.9 |
| `SessionType`, `IStreamSessionHandler`, `IStreamSession`, `IStreamChannel` | stream sessions: one WebSocket | §7.10 |
| `SessionType.Stateful`, `IPluginSessions`, `IPluginSession` | sessions your own endpoints carry, registered with core | §7.17 |
| `SessionOrigin`, `ISessionOriginHandler`, `ISessionOrigins` | a session served on its own origin, as web pages are | §7.18 |
| `IPluginAudit` | your own audit events | §7.12.1 |
| `definePlugin`, `sessionViews`, `SessionViewProps` | the frontend entry and session tabs | §5, §7.14 |
| `api`, `openStreamSession`, `adoptStyles`, `mirrorInjectedStyles` | frontend helpers | §7.14 |

**Versions.** Declare the SDK range you support in the manifest. SDK v1 is frozen: an addition raises the minor version (1.1.0), and a breaking change would raise the major one. So `>=1.0.0 <2.0.0` keeps loading on every v1 server. If you use something added later, raise the lower bound to the version that brought it.

## 7. Checklist

- References only `NKeeper.Abstractions` and `@nkeeper/sdk`.
- Every endpoint that acts on a host is in a host group, with the permission it needs: `Use` to connect or read, `Manage` to change.
- Every service ID from a request is checked against the host's services and your type.
- Options and messages from the browser are validated and bounded.
- Connection failures are left to core when they carry a server identity to trust.
- Your own operations are audited under your ID; no secrets, ever, in audit details, logs or responses.
- Sessions return promptly when cancelled, and dispose what they opened.
