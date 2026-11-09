# NKeeper

NKeeper is a self-hosted, browser-based remote connection manager for teams. Keep an inventory of your hosts with their services, credentials and tags, then open SSH, RDP, VNC, web, VM-console and database sessions in browser tabs. The NKeeper server makes every connection, so passwords and keys never reach the browser.

> **Status: NKeeper 2.0 is in early development.** This repository is a modular, plugin-based rewrite. Nothing here is usable yet.
> For a working release, use **[NKeeper 1.x](https://github.com/DHemken97/NKeeper)**.

## Why 2.0

NKeeper 1.x covers a lot: SSH and Telnet terminals, RDP and VNC through Apache Guacamole, proxied web interfaces, KVM and Proxmox consoles and migrations, Dell iDRAC, Linux and Windows admin tools, SQL Server, Oracle and SAP ASE tools, Zabbix, scans, health checks, audit and single sign-on. All of it lives in one application, so adding a new kind of service means touching code throughout it.

2.0 keeps the same stack and security model and changes the shape:

- A small **core** owns everything about trust: identity and access, the credential vault, audit, session lifecycle, SSH connectivity and jump hosts, and pinning of host keys and certificates.
- Everything else is a **plugin**: a self-contained folder with its own backend, frontend, tests and docs, built against a versioned SDK. That includes essentials like SSH terminals and remote desktops, which ship as built-in plugins.
- Plugins are **loaded at run time from signed packages**. Only root on the server can install one, and every file is hash-checked and every signature verified before any plugin code runs. See [docs/plugin-security.md](docs/plugin-security.md).

> Core provides capabilities. Plugins consume capabilities and contribute functionality. Core never needs to know which plugins exist.

The full design, including the audit of 1.x, the extension points, the data model and the phased plan, is in **[docs/v2-plan.md](docs/v2-plan.md)**. How it is built today, and the rules that keep core and plugins apart, is in [docs/architecture.md](docs/architecture.md).

## Technology

| Area | Choice |
| --- | --- |
| Backend | ASP.NET Core on .NET 10, Entity Framework Core, PostgreSQL |
| Frontend | React, TypeScript, Vite |
| Remote desktops | Apache Guacamole `guacd` |
| Tests | xUnit with real servers in Testcontainers; Vitest and Testing Library |
| Deployment | Native Linux service: systemd, nginx and PostgreSQL on Debian/Ubuntu or RHEL-family distributions |

## Contributing

Contributions are welcome. Read **[CONTRIBUTING.md](CONTRIBUTING.md)** first. In particular, design changes go into the design documents before any code.

## Security

Please don't report vulnerabilities in public issues. See **[SECURITY.md](SECURITY.md)**.

## License

[MIT](LICENSE)
