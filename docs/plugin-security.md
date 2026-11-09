# Plugin loading and package security

Status: design, before implementation (Phase 0).
Part of the [NKeeper 2.0 plan](v2-plan.md); see decision D1 there.

NKeeper loads plugins at run time from signed packages. A plugin runs **inside** the NKeeper server process, with the same privileges, including access to the credential vault. Loading a plugin is therefore equivalent to installing code into NKeeper itself. This document defines how a plugin gets onto a server, how it is verified, and when its code may run.

## 1. Principles

1. **Fail closed.** If any check fails, that plugin's code never runs. There is no "load anyway" switch and no unsigned mode, not even in development.
2. **Root installs, admins enable.** Placing a plugin on a server requires root on that machine. An NKeeper administrator can only enable or disable plugins root has already installed. There is no upload through the web UI or the API.
3. **Verify what you load, load what you verified.** The package is read into memory once, verified there, and code is loaded from those same verified bytes. Nothing is re-read from disk between verification and loading.
4. **The process can't modify its own plugins.** Even a fully compromised NKeeper process (an attacker with code execution as the service user) can't write to the plugin directory or the trust configuration.
5. **In-process is not a sandbox.** Separate load contexts keep plugins' library versions apart. They do not limit what a plugin can do. Trust is decided by signatures, and untrusted code would need the out-of-process design (plan, Phase 8).

## 2. Threat model

| Attacker can… | Mitigation |
| --- | --- |
| Run code as the NKeeper service user (an RCE in NKeeper or a plugin) | Plugin directory and trust files are owned by root and not writable by the service user. systemd `ProtectSystem=strict` makes them read-only to the process regardless. (§4) |
| Modify a file inside a package | Every file's SHA-256 is listed in the signed file list. Any mismatch refuses the plugin. (§5) |
| Forge a package | ECDSA signature over the file list, checked against trusted keys only. (§5, §6) |
| Use a compromised third-party key to replace an official plugin (e.g. a fake `ssh`) | Keys are scoped to plugin ID namespaces. Unprefixed IDs are reserved for the official key. (§6) |
| Replace a package with an older, validly signed version that has a known flaw | Installed versions are pinned. Downgrades are refused unless root explicitly allows one. (§8) |
| Ship a different package under the same version number | Each package hash is pinned. The same ID and version with a different hash is refused as tampering. (§8) |
| Take over an NKeeper administrator account | Can enable or disable plugins root already installed, and nothing more. Every change is audited. (§9) |
| Craft a malicious ZIP (path traversal, duplicate entries, decompression bombs) | Strict package parser with limits, applied to every entry, the signature metadata included, before anything is decompressed or authenticated. Paths are normalized and validated before use. (§5.2) |
| Swap files between verification and loading | Not possible: verification and loading use the same in-memory bytes. (§7) |
| Get a signed package that is itself malicious (a trusted publisher goes bad) | Out of scope for in-process loading, so only trust keys you would trust with the vault. Key scoping limits the damage, and keys and packages can be revoked. (§6.3) |
| Root on the NKeeper server | Out of scope. Root can replace NKeeper itself. |

## 3. Package format (`.nkp`, format version 1)

A package is a ZIP file named `<plugin-id>-<version>.nkp`:

```
nkeeper-plugin.json        manifest
files.json                 signed list of every other file: path, size, SHA-256
files.json.sig             signature over the exact bytes of files.json
backend/<Assembly>.dll     the plugin's own assemblies and its private dependencies
frontend/<entry>.js, …     pre-built ES modules and assets
```

**Manifest** (`nkeeper-plugin.json`):
- `formatVersion`, `id`, `name`, `version` (SemVer 2.0), `sdkVersion` (the range it is compatible with);
- `dependsOn` (IDs with version ranges), `requirements` (e.g. `guacd`);
- `backend.entryAssembly` and `backend.entryType` (the `IPlugin` implementation);
- `backend.exportedAssemblies`: contract assemblies dependants may use, such as `db-core`'s engine interfaces;
- `frontend.entry` (optional: a plugin may have no frontend);
- `builtIn` (optional, default `false`): ship-with-NKeeper plugins such as `ssh`. It is honoured **only** when the package is signed by an official key, or by a development key on a server running in `Development`. From any other signer it is ignored, so a third-party plugin can never make itself undisableable. This is how the server knows what is built in without core naming any plugin.

A **version range** is one or more comparators separated by spaces, all of which must hold: `>=0.1.0 <0.2.0`. The operators are `=`, `>`, `>=`, `<` and `<=`, with SemVer 2.0 precedence.

**File list** (`files.json`): a canonical JSON document with the format version, the plugin ID and version (repeated so they are covered by the signature), and one entry per file in the package except `files.json` and `files.json.sig`, sorted by path:

```json
{ "formatVersion": 1, "id": "ssh", "version": "2.0.0",
  "files": [ { "path": "backend/NKeeper.Plugins.Ssh.dll", "size": 48128, "sha256": "…" }, … ] }
```

**Signature** (`files.json.sig`): `{ "keyId": "…", "algorithm": "ECDSA-P256-SHA256", "signature": "<base64>" }`. ECDSA P-256 with SHA-256 is built into .NET, so no third-party cryptography library is involved. The signature is the 64-byte IEEE P1363 form (`r ‖ s`). A key's `keyId` is the lowercase hex of the first 16 bytes of the SHA-256 of its public key (DER SubjectPublicKeyInfo), so an ID can't be claimed for a different key.

Native libraries are **not allowed** in format version 1 (`native_library`): every `.dll` must be a managed assembly, and `.so`, `.dylib` and `.exe` files are refused. If a plugin ever needs native code, the format gets a version 2 with a defined extraction step.

The archive itself is deliberately plain: no ZIP64, no data descriptors, no encryption, no directory entries, no spanning, and file names in UTF-8. The packaging tool writes exactly that, and the verifier accepts nothing else.

## 4. Where packages and keys live, and who may write there

| Path | Owner / mode | Contents |
| --- | --- | --- |
| `/usr/lib/nkeeper/plugins/` | `root:root` `0755` | installed `.nkp` packages, `root:root` `0644` |
| `/etc/nkeeper/plugin-trust/` | `root:root` `0755` | additional trusted keys and the local revocation list, `root:root` `0644` |
| `/var/lib/nkeeper/` | `nkeeper:nkeeper` `0750` | the only place the service may write (diagnostics, temporary files) |

At startup, before reading any package, the server checks the plugin and trust directories, every ancestor directory, and every file in them, without following symlinks (Linux `statx` with `AT_SYMLINK_NOFOLLOW`, whose result layout is the same on every architecture):
- the owner is `root`;
- none is writable by group or others;
- none is a symlink;
- the files are regular files.

If either directory fails, **no** plugin loads, built-in ones included, and a security alert is raised (§9).

The trust directory can't be partially trusted. It also holds the local revocation list (§6.3), so carrying on with only the embedded official key would silently undo any local revocation of an official key or package. The server still starts with core alone, so an administrator can see the alert, unless `Plugins:RequireAllEnabledLoaded` is set (§9).

The server **refuses to run as root** in production. The systemd unit installed in Phase 6 runs it as the unprivileged `nkeeper` user with at least:
- `ProtectSystem=strict` and `ReadWritePaths=/var/lib/nkeeper`;
- `NoNewPrivileges=yes`, `PrivateTmp=yes`, `ProtectHome=yes`;
- an empty `CapabilityBoundingSet`.

Even code execution inside NKeeper can't change what it loads next time.

**Development** (`ASPNETCORE_ENVIRONMENT=Development`) on a non-Linux machine skips only the ownership and mode checks, and logs a warning on every start. Signatures and hashes are always checked, everywhere. **Outside Development, on any OS other than Linux, no plugin loads**, since the checks above can't be made there.

## 5. Verifying a package

Packages are verified in this order. The first failure refuses the plugin with a specific reason code (shown in §9).

### 5.1 Read

1. The file is a regular file, and its name matches `<id>-<version>.nkp` (`bad_name`).
2. Its size is at most 200 MB (`too_large`). It is read fully into memory and then closed. Everything below works on those bytes.
3. The SHA-256 of the whole package is computed and kept for pinning (§8).

### 5.2 Structure and limits, before reading any entry

Nothing is decompressed until the whole ZIP structure passes, including the signature metadata. A decompression bomb or malformed archive is refused before authentication, not after.

4. Only the central directory is parsed. It must be well formed, and each entry's local header must agree with it. Entries' data may not overlap or extend into the central directory (`bad_zip`).
5. Every entry is checked (`bad_entry`):
   - its path is relative, uses `/`, and contains no `..`, empty or `.` segments, drive letters, backslashes or control characters;
   - no two entries have the same path, including paths that differ only in case;
   - it is not a directory, a symlink or an encrypted entry, and it doesn't use ZIP64 or a data descriptor;
   - its compression method is *stored* or *deflate*.
6. Declared limits (`limits`):
   - at most 5,000 entries and 500 MB uncompressed in total;
   - a compression ratio of at most 100:1 per entry;
   - metadata caps: `nkeeper-plugin.json` at most 64 KB, `files.json` at most 1 MB, `files.json.sig` at most 4 KB.
7. Every entry is decompressed through a bounded reader. It stops and refuses as soon as the output passes the entry's declared size, or the limits above (`limits`). This applies to the metadata files as well, so declared sizes are never trusted on their own. Output shorter than declared, or a CRC-32 mismatch, is `bad_zip`.

### 5.3 Signature

8. `files.json` and `files.json.sig` exist (`unsigned`).
9. The `keyId` belongs to a trusted, unrevoked, unexpired key (`untrusted_key`, `revoked_key`, `expired_key`).
10. The signature over the exact bytes of `files.json` is valid for that key (`bad_signature`).
11. `files.json` parses as a file list of a supported format version (`bad_file_list`). It is parsed only after its signature is verified.
12. The key is allowed to sign this plugin ID in this environment (§6.2) (`key_scope`).
13. The `id` and `version` inside `files.json` match the file name, which must be exactly `<id>-<version>.nkp` (`identity_mismatch`). The manifest is checked against them in step 16.

### 5.4 Contents

14. The set of entries is **exactly** `files.json`'s list plus the two signature files: nothing missing, nothing extra (`file_set`).
15. Every file's size and SHA-256 match its entry in the list (`hash_mismatch`).

### 5.5 Manifest and compatibility

16. The manifest parses, its `formatVersion` is supported, its `id` follows the ID rules (§6.2), and its entry assembly is in the package (`bad_manifest`). Its `id` and `version` match `files.json` (`identity_mismatch`).
17. Its `sdkVersion` range includes this server's SDK version (`sdk_incompatible`).
18. It contains no native code (§3) (`native_library`): every assembly must be IL-only, with no native entry point or precompiled native code, and no copy of an assembly that core shares with plugins (§7.1), judged by each assembly's metadata name, not its file name (`shared_assembly`).
19. The revocation lists don't name this package's hash (`revoked_package`).
20. Pinning and downgrade rules pass (§8) (`downgrade`, `pin_mismatch`).

### 5.6 Dependencies

21. Every `dependsOn` entry is present, verified, enabled and in range. Otherwise this plugin is refused too (`dependency`). Dependency cycles are refused (`dependency_cycle`).

Only a plugin that passes all of these **and** is enabled (§8) has its code loaded.

### 5.7 Refusals by the plugin host

The host refuses for reasons that concern the server rather than one package:

| Code | When | What doesn't load |
| --- | --- | --- |
| `unsupported_platform` | Outside Development, on an OS other than Linux (§4) | every plugin |
| `unsafe_location` | The plugin or trust directory, an ancestor, or a file in them fails the checks of §4 | every plugin |
| `bad_trust_config` | A key file or the revocation list in the trust directory can't be used | every plugin |
| `duplicate_id` | Two packages in the directory carry the same plugin ID | every package with that ID |
| `load_failed` | The verified code can't be loaded: the entry type is missing, doesn't implement `IPlugin`, or its constructor or `ConfigureServices` throws | that plugin, and its dependants |

**Testing the ownership checks.** CI runs tests as an unprivileged user, who can't create root-owned directories. In Development only, `Plugins:DevelopmentOwnerUid` names the owner the checks expect instead of root. Every directory and ancestor must then be owned by root or by that user, and no group- or other-writable bit is ever allowed. Outside Development the setting is ignored and the owner is always root.

## 6. Trust

### 6.1 Keys

- **Official key(s):** the public keys are embedded in the NKeeper server binary for each release. Built-in plugins (ssh, desktop, web…) are signed with them.
- **Additional keys:** one JSON file per key in `/etc/nkeeper/plugin-trust/`, root-owned:
  ```json
  { "keyId": "…", "publicKey": "<SPKI base64>", "allowedIds": ["com.example.*"], "notAfter": "2028-01-01", "comment": "Example Corp plugins" }
  ```
- **Development keys:** created by the packaging tool on a developer's machine (and fresh for each CI run), and marked `"development": true`. They exist so that local and CI builds of the built-in plugins load without the release key.
  - **In `Development`** (and the test environment), a development key in the configured development trust directory may sign official, unprefixed IDs. This is the only way anything but an official key can sign them.
  - **In any other environment**, development keys are refused outright, whatever they claim to be allowed to sign (`untrusted_key`). So this authority can't exist in production.
  - The development trust directory is configurable only in `Development`. Production always uses `/etc/nkeeper/plugin-trust/`.

### 6.2 Plugin IDs and key scope

- IDs are lowercase `[a-z0-9-]` segments separated by dots.
- **IDs without a dot** (`ssh`, `db-mssql`, `zabbix`) are reserved for official plugins. Only an official key may sign them, or a development key on a server running in `Development` (§6.1).
- Everyone else uses a reverse-domain ID (`com.example.netbox`). A non-official key may sign only IDs matching its `allowedIds` patterns, and a pattern must name at least two fixed segments before any wildcard (`com.example.*` is allowed, `com.*` and `*` are not).

So a compromised or careless third-party key can never stand in for an official plugin, or for another publisher's.

### 6.3 Revocation

- Each server release embeds a list of revoked key IDs and package hashes.
- `/etc/nkeeper/plugin-trust/revoked.json` lets root add more locally.
- A revoked key or package is refused at the next start. Revoking a key also refuses every package signed with it.

The official release key exists in exactly two copies (decision **D12**):
- a secret in a protected GitHub Actions environment that needs maintainer approval for every release;
- an offline backup held by the maintainer.

The maintainer generates it locally with `NKeeper.PluginTool keygen --release`. It is never generated by automation and never committed. Each release embeds the current public key and the next one, so the key can be rotated without breaking installed servers. Until the first release, builds use development keys only.

## 7. Loading

### 7.1 Backend

- Each plugin gets its own `AssemblyLoadContext`, loaded from the verified in-memory bytes (`LoadFromStream`). No plugin assembly is ever loaded from a path.
- **Shared assemblies** always resolve from the server's default context. The shared set is exactly what that context provides (its trusted platform assemblies): the .NET runtime, ASP.NET Core, `NKeeper.Abstractions` and core's own dependencies such as EF Core and Npgsql. A package that contains its own copy of one is refused (§5.5), so a plugin can't swap in a different ASP.NET or EF. Plugin projects reference shared packages without copying them (`ExcludeAssets="runtime"`).
- A plugin's private dependencies, such as a database driver, load in its own context. A dependant sees only the assemblies its dependency lists in `exportedAssemblies`.
- **Installing, updating, enabling or disabling a plugin takes effect at the next restart.** Code is never loaded into or unloaded from a running server. Unloading code that has registered routes, EF models and background services isn't reliable, so NKeeper doesn't try.

### 7.2 Frontend

- Plugin frontend files are served from the verified in-memory copy, never from disk, at `/plugins/<id>/<version>/<first 12 hex digits of the package SHA-256>/<path inside frontend/>`:
  - only for a **loaded** plugin, and only files under `frontend/` in its package;
  - only file types on an allow-list: `.js`, `.mjs`, `.css`, `.json`, `.map`, `.svg`, `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.ico`, `.woff`, `.woff2`. Anything else is 404;
  - with an explicit content type, `X-Content-Type-Options: nosniff` and `Cache-Control: public, max-age=31536000, immutable`. The hash prefix in the URL means a new package never reuses a cached URL.
- The shell provides React and `@nkeeper/sdk` through an import map, so plugins share one copy and can't bring their own. The shared modules are `react`, `react/jsx-runtime`, `react-dom`, `react-dom/client` and `@nkeeper/sdk`. They are built once, by `@nkeeper/runtime`, as hashed ES modules that the shell's own build also imports by the same names, so there is exactly one React in the page.
- Integrity: browsers can't attach Subresource Integrity to `import()`, and an import map can't gain entries after the first module loads. Serving only verified bytes is the control, and `GET /api/plugins` publishes each entry module's SHA-256 so it can be checked if browser support arrives.
- **The import map is the one inline script,** and the Content Security Policy allows it by hash only:
  - its content is fixed for a server build, since it maps only React and `@nkeeper/sdk` to the shell's own hashed files, never plugin files;
  - the server computes its SHA-256 at startup and sends `script-src 'self' 'sha256-<hash>'`.
  
  Any other inline script, and `eval`, stays blocked. **The server refuses to start** if the shell's `index.html` contains any inline script other than exactly one import map, so a build mistake can't quietly widen the policy. Plugins are loaded by `import()` of same-origin URLs, so they never need an import map entry or an inline script. (Browsers can't load an import map from an external file, which is why it has to be inline and allowed by hash.)

### 7.3 Who serves the shell

The NKeeper server itself serves the shell (`index.html` and its hashed assets, from `Frontend:Root`) with the security headers, so the policy and the import map hash are decided in one place. The full set of headers:
- `Content-Security-Policy`: `default-src 'self'; script-src 'self' 'sha256-<import map>'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`;
- `X-Content-Type-Options: nosniff`;
- `Referrer-Policy: no-referrer`.

nginx, in the Phase 6 installer, terminates TLS and proxies, but doesn't serve files.

In development the shell is built with `--watch` and the server serves it, so plugins load exactly as in production, under the real policy. The Vite dev server can still be used for work on the shell alone, but plugins don't load there: it can't share one React with modules imported at run time.

## 8. Installed, enabled, pinned

| State | Meaning |
| --- | --- |
| **Installed** | A package is in the plugin directory and passes verification. Its manifest is read, and its code is not loaded. |
| **Enabled** | An administrator has turned it on in NKeeper. Its code loads at startup, its migrations run, and its endpoints and contributions appear. Built-in plugins (§3, `builtIn`) are enabled the first time they are seen and can't be disabled. |
| **Disabled** | Its code doesn't load. Its data and settings are kept. Its service types show as unavailable. |
| **Refused** | Verification failed. Its code doesn't load, it is shown with the reason code, and an alert is raised (§9). |
| **Awaiting signer approval** | A newer version signed by a different key than the recorded one. Its code doesn't load until an administrator approves the new signer. |

Core records every plugin it has seen in `core.installed_plugins`:
- `id`, `version`, `package_sha256`, `key_id`, whether it is built in;
- whether it is enabled, and who enabled it, and when;
- when it was first seen and last updated.

A plugin seen for the first time is recorded and audited (`plugin.discovered`). A built-in plugin starts enabled; any other starts disabled until an administrator enables it.

At every start:
- **same version, same hash:** loads;
- **same version, different hash:** refused (`pin_mismatch`), treated as tampering;
- **newer version, same signing key:** an update. It loads and the record is updated, with the update audited;
- **newer version, different signing key:** awaiting signer approval. An administrator must approve the new key before it loads;
- **older version:** refused (`downgrade`), unless root has run `nkeeper plugin allow-downgrade <id> <version>`, which allows exactly that version once.

Until administrators exist (Phase 1), nothing can enable a plugin or approve a signer, so only built-in plugins load. The `allow-downgrade` command arrives with the rest of the command line in Phase 6; until then a downgrade is always refused.

Removing a package from the directory keeps the plugin's data. The plugin shows as missing until its package returns.

## 9. Operations and visibility

**Command line.** Installing is a root operation on the server, through the server binary's `plugin` command:
- `verify <file>`: runs every check in §5 without installing, and prints the result and the reason codes;
- `install <file>`: verifies, then copies the package into the plugin directory as `root:root 0644` with an atomic rename. Restart NKeeper to load it;
- `remove <id>`, `list`, `allow-downgrade <id> <version>`, `trust add|remove|list`.

**Admin UI.**
- **Shows:** every plugin with its state, version, signer, package hash, requirements and refusal reasons.
- **Allows:** enable, disable, and approving a new signing key for an update.
- **Does not offer:** upload, install or remove.

**Audit.** Plugin events are core-owned audit events:
- `plugin.discovered`, `plugin.enabled`, `plugin.disabled`, `plugin.updated`, `plugin.loaded`;
- `plugin.refused`, with the reason code. This also raises a high-severity alert in diagnostics and a banner for administrators.

**Startup order.** The core database schema is migrated first, so plugin state can be read and recorded. Then the plugin host checks the directories, loads the trust configuration, verifies every package, applies pinning and dependencies, and loads the enabled plugins' code. Only then is the web application built, with the loaded plugins' services and endpoints.

**Startup.** By default a refused plugin doesn't stop the server. The rest of NKeeper starts without it (and without its dependants), and the alert is raised. Setting `Plugins:RequireAllEnabledLoaded=true` makes the server refuse to start if any *enabled* plugin is refused, for operators who'd rather be down than run without a security-relevant plugin.

## 10. Tooling

`tools/NKeeper.PluginTool` is a thin command line over `NKeeper.Plugins.Packaging`, the library that also holds the server's verifier, so the tool and the server can't disagree:
- `keygen`: a development key by default, or `--release` for the official key (D12). The private key is written with owner-only permissions;
- `pack`: builds the canonical `files.json` and the archive, and signs it in the same step. Everything but the signature is deterministic: ECDSA signatures are randomized, so two builds of the same plugin differ only in `files.json.sig`;
- `verify`: runs the same checks as the server and prints the reason code.

The tool can't see a server's exact shared-assembly set (§7.1). By default it treats the .NET runtime and the ASP.NET Core shared framework as shared. `--shared-list <file>` adds names, one per line, for example a list exported from the target server. The server's own check at load time is the one that counts.

The repository's build packs and signs the built-in plugins with a development key, so the development loop never needs an unsigned mode.

## 11. Tests (Phase 0)

Every refusal path has a test that builds a real package and expects that exact reason code:
- **Signature:** unsigned, untrusted key, revoked key (embedded and local), expired key, bad signature, key outside its scope, a third-party key signing an official ID, a development key signing an official ID (accepted in `Development`, refused in production), and a development key refused outright in production.
- **Identity:** mismatches between the file name, the file list and the manifest.
- **Contents:**
  - a tampered file, a missing file, an extra file;
  - duplicate and case-colliding paths, path traversal, absolute paths, symlink entries;
  - an encrypted entry, an unsupported compression method;
  - too many entries, too large, a decompression bomb in a data file, **and in each metadata file** (`files.json`, `files.json.sig`, the manifest), refused before any signature check;
  - a malformed central directory, and a local header that disagrees with the central directory.
- **Manifest and compatibility:** an incompatible SDK version, a bundled shared assembly, a revoked package hash.
- **Versions:** a downgrade, the same version with a different hash, a newer version with a different signing key.
- **Dependencies:** a missing, disabled or refused dependency, a dependency cycle.
- **Filesystem (on Linux):**
  - a writable plugin directory or file;
  - an owner other than root;
  - a symlinked directory or package;
  - a writable ancestor directory;
  - a trust directory that fails its checks: every plugin refused, built-in ones included, with the alert raised;
  - running as root in production.
- **Frontend:** the served Content Security Policy allows the import map by its hash, and blocks any other inline script.
- **Positive paths:** a valid package loads; disabled stays unloaded; an update with the same key loads and is audited.
