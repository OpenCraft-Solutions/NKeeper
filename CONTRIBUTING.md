# Contributing to NKeeper

Thanks for your interest in NKeeper. NKeeper 2.0 is in early development, so the design is still settling. Opening an issue to discuss an idea before writing code saves everyone time.

## Ground rules

### Design documents come first

The design lives in [`docs/`](docs/): [`v2-plan.md`](docs/v2-plan.md) and [`plugin-security.md`](docs/plugin-security.md) today, and `architecture.md` and `plugin-authoring.md` as they are written. **If a change affects the design, update the design documents first, in their own commit, before changing any code.** Design changes include:

- the core/plugin boundary or the plugin SDK;
- the data model;
- dependencies between plugins;
- security rules;
- the phases or the decisions table.

Implementation details that don't contradict the documents don't need a doc change.

### Branches and pull requests

- `main` is protected. Every change goes through a pull request; nobody pushes to `main` directly.
- Work on a topic branch (`feature/…`, `fix/…`, `docs/…`, `chore/…`) and commit in small, logical steps.
- Pull requests are squash-merged, so the PR title becomes the commit message on `main`. Write it as a short imperative summary ("Add the Telnet plugin").
- Copilot reviews every pull request automatically. Its comments are suggestions: address the ones that improve the change, and reply briefly to the ones you decline.

### Respect the plugin boundaries

- Core must never refer to a specific service type, plugin or product.
- A plugin may depend only on `NKeeper.Abstractions` (the SDK) and on plugins listed in its manifest. It never depends on core internals.
- Plugins get hosts and credentials only through the core services that enforce access (`IHostDirectory`, `ICredentialResolver`, host-scoped endpoints). Never through the database directly.

### Tests

- Backend tests run against real servers (PostgreSQL, OpenSSH, guacd and so on) in [Testcontainers](https://dotnet.testcontainers.org/). You need Docker to run them. This is the only place NKeeper uses Docker: it's installed natively on Linux, never as a container.
- Changes to plugin loading or package verification come with a test for every new refusal path (see [plugin-security.md](docs/plugin-security.md#11-tests-phase-0)).
- New behaviour comes with tests. A bug fix comes with a test that fails without it.
- When porting code from 1.x, port its tests with it.

### Keep the repository public-safe

This is a public repository. Never commit:

- secrets, passwords, keys or certificates (other than test fixtures made for the purpose);
- real hostnames, IP addresses, or anything describing a real network or organization.

Use `example.com`/`*.example` names and the documentation address ranges (`192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24`, `2001:db8::/32`). Secret scanning with push protection is enabled on this repository.

## Development setup

You need:

- the .NET 10 SDK (the exact version is pinned in [`global.json`](global.json));
- Node.js 24 LTS, 24.15 or later (see [`.nvmrc`](.nvmrc)); Node 22.22.2+ and 26+ also work. These follow the strictest requirement among the locked dependencies (currently `jsdom`);
- Docker, for the test suite only: the tests start PostgreSQL and other servers in containers;
- PostgreSQL 17, to run the server locally.

**Backend** (from the repository root):

```bash
dotnet restore NKeeper.slnx
dotnet build NKeeper.slnx
dotnet test NKeeper.slnx
dotnet format NKeeper.slnx                      # fix formatting; CI runs it with --verify-no-changes
```

To run the server, give it a database in [.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets), outside the repository. It creates its schema on start:

```bash
dotnet user-secrets --project core/backend/NKeeper.Server set "ConnectionStrings:NKeeper" "Host=localhost;Database=nkeeper;Username=nkeeper;Password=<yours>"
dotnet run --project core/backend/NKeeper.Server   # http://localhost:5080
```

It starts without plugins until one is installed. In Development you can point `Plugins:Directory` at a folder of packages you built with `nkeeper-plugin`, and `Plugins:DevelopmentTrustDirectory` at the folder holding your development key's `.trust.json` (see [plugin-security.md §4, §5.7](docs/plugin-security.md)).

NuGet versions are managed centrally in [`Directory.Packages.props`](Directory.Packages.props), and every project has a `packages.lock.json`. CI restores in locked mode, so commit the lock files whenever a dependency changes. Schema changes are EF Core migrations, committed with the code: `dotnet tool restore`, then `dotnet ef migrations add <Name> --project core/backend/NKeeper.Core.Persistence --output-dir Migrations`.

**Frontend** (from the repository root, which is an npm workspace):

```bash
npm install
npm run lint            # oxlint; warnings fail the build
npm run typecheck
npm test
npm run build           # @nkeeper/runtime (the shared modules and import map), then the app
```

The frontend packages are:
- `core/frontend/sdk`: `@nkeeper/sdk`, what plugin frontends build against;
- `core/frontend/runtime`: `@nkeeper/runtime`, which builds the modules the shell shares with plugins (React, ReactDOM, the JSX runtime, the SDK) and the import map naming them;
- `core/frontend/app`: the shell.

**Running the shell with plugins.** Plugins load only through the NKeeper server, under the real Content Security Policy ([plugin-security.md §7.3](docs/plugin-security.md#73-who-serves-the-shell)). Build the frontend, then point the server at it. `watch` rebuilds the shell as you edit:

```bash
npm run build
npm run watch --workspace @nkeeper/app &
dotnet user-secrets --project core/backend/NKeeper.Server set "Frontend:Root" "$(pwd)/core/frontend/app/dist"
dotnet run --project core/backend/NKeeper.Server   # http://localhost:5080
```

`npm run dev --workspace @nkeeper/app` (Vite on :5173, proxying `/api`) is still there for work on the shell alone, but plugins don't load in it.

**Plugin packages** are built and checked with the `nkeeper-plugin` tool (see [plugin-security.md §10](docs/plugin-security.md#10-tooling)):

```bash
dotnet run --project tools/NKeeper.PluginTool -- --help
```

CI runs all of the above on every pull request, along with CodeQL and a dependency review.

Plugins are always signed, even in development. The build creates a local development key and signs the built-in plugins with it, so there is no unsigned mode to switch on.

## Reporting bugs and requesting features

Use the issue templates. For security problems, follow [SECURITY.md](SECURITY.md) instead of opening an issue.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
