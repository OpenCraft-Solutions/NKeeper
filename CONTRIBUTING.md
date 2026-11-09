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

The tooling is being set up in Phase 0 of the [plan](docs/v2-plan.md#8-gameplan). This section will describe building, running and testing once there is code. Expect:

- the .NET 10 SDK;
- Node.js 22 or 24 LTS;
- PostgreSQL, to run the server locally;
- Docker, for the test suite only.

Plugins are always signed, even in development. The build creates a local development key and signs the built-in plugins with it, so there is no unsigned mode to switch on.

## Reporting bugs and requesting features

Use the issue templates. For security problems, follow [SECURITY.md](SECURITY.md) instead of opening an issue.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
