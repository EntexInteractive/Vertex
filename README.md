# [<img src="https://raw.githubusercontent.com/EntexInteractive/Vertex/master/Vertex.Cli/vertex-icon.ico" alt="Vertex Icon" width="32" height="32">](https://github.com/EntexInteractive/Vertex) Vertex

[![.NET](https://img.shields.io/github/actions/workflow/status/EntexInteractive/Vertex/build-latest.yml?label=Main%20build&style=for-the-badge)](https://github.com/EntexInteractive/Vertex/actions/workflows/build-latest.yml) [![latest version](https://img.shields.io/github/v/release/EntexInteractive/Vertex?label=Latest%20release&style=for-the-badge)](https://github.com/EntexInteractive/Vertex/releases/latest) [![GitHub Downloads](https://img.shields.io/github/downloads/EntexInteractive/Vertex/total?style=for-the-badge)](https://github.com/EntexInteractive/Vertex/releases/latest) [![Docker Pulls](https://img.shields.io/docker/pulls/entexinteractive/vertex-server?style=for-the-badge)](https://hub.docker.com/r/entexinteractive/vertex-server)

> [!WARNING]
> Vertex is **early development**. A lot of the core systems are still being built. Don't trust it blindly with your only copy of anything.

## What is Vertex?

**Vertex is an open-source, self-hosted development platform designed to bring source control, code review, build automation, issue tracking, and development workflows together into one unified ecosystem.** Built with binary assets in mind, Vertex aims to combine the centralized version control capabilities of Perforce with the simplicity of Git and the integrated development workflows of modern DevOps platforms.

At its core, Vertex provides a centralized source control system powered by content-addressable storage (CAS), deduplication, content-defined chunking, and high-performance gRPC file transfers. It is designed to efficiently manage large repositories, binary assets, and collaborative development projects. Beyond source control, Vertex's long-term vision is to provide an all-in-one development environment where submitting changes, reviewing code, building projects, tracking issues, and automating workflows are all seamlessly connected.

Whether you're an independent developer working on your first game or a studio managing multiple projects, the goal is the same: **spend less time managing development infrastructure and more time building.**

## Why Vertex?

Modern game development often relies on multiple disconnected tools to manage the development lifecycle. Large teams commonly use Perforce for source control, Swarm for code reviews, Jira for issue tracking, Horde or TeamCity for build automation, and Robomerge for branch integration. While these tools are powerful individually, maintaining and integrating them introduces additional complexity, infrastructure overhead, and complicated administrative work.

Rather than managing separate applications and integrations, Vertex is designed around one unified development workflow:

**Submit → Review → Build → Integrate**

Submitted revisions are held for review, built, and then, if a build fails, Vertex's planned automation will automatically create or update an issue, attach relevant diagnostic data, and link the failure to the originating change. Developers can resolve the problem and continue the workflow without switching between multiple platforms.

### Our Goals

- **Unified Development:** Bring source control, code review, CI/CD, issue tracking, and automation into one cohesive platform.
- **Simplicity First:** Provide intuitive workflows and straightforward administration without sacrificing powerful capabilities.
- **Built for Large Projects:** Efficiently handle large files, binary assets, and repositories commonly found in game development.
- **Self-Hosted by Design:** Your projects, your code, and your infrastructure under your control.
- **Automation at Every Step:** Reduce repetitive tasks by connecting changes, reviews, builds, and issues automatically.
- **Open Source:** Encourage transparency, collaboration, and community-driven development without vendor lock-in.

## Feature Status

| Feature | Status | Notes/Roadmap |
| :--- | :--- | :--- |
| Source Control | 🟥 Early | Centralized version control with branches, revisions, and merging. Complete synchronization, conflict resolution, and file locking. |
| Branch Automation | ⬜ Planned | Automated branch integration and change propagation, similar to Epic Games' Robomerge. |
| Content-Addressable Storage | 🟧 Experimental | SHA-256 addressing, FastCDC chunking, deduplication, and Zstandard compression. Improve integrity verification and garbage collection. |
| File Transfers | 🟧 Experimental | gRPC-based uploads and downloads with S3-compatible storage. Improve performance, reliability, and the ability to resume transfers. |
| Command-Line Interface | 🟧 Experimental | Core `vx` commands for interacting with Vertex. Expand repository management and source-control workflows. |
| Web Dashboard | 🟥 Early | Centralized project management, administration, and monitoring. |
| Desktop Client | 🟥 Early | Graphical interface for source-control operations and web dashboard. |
| Code Review | ⬜ Planned | Review submitted changes with comments, approvals, and branch protection. |
| Build Automation | ⬜ Planned | CI/CD pipelines, distributed build agents, automated testing, and artifact management. |
| Issue Tracking | ⬜ Planned | Integrated bug reports, tasks, project boards, and automatic issue creation from failed builds. |
| Unreal Engine Integration | ⬜ Planned | Native Unreal Editor integration, asset locking, and game-development workflows. |

## Key

- 🟩 **Production Ready**: Feature is stable, tested, and recommended for production environments. Compatibility and upgrade paths are maintained between releases.
- 🟨 **Beta**: Feature is functional and suitable for evaluation, but may contain bugs or minor missing functionality. Breaking changes are expected to be limited.
- 🟧 **Experimental**: Feature is functional but may undergo significant changes or be removed in future releases. Not recommended for critical production workloads.
- 🟥 **Early**: Feature is in active or initial development and may be incomplete, unstable, or subject to major redesign.
- ⬜ **Planned**: Feature is on the roadmap but has not yet been implemented.

## Quick start

You need [Docker](https://docs.docker.com/get-docker/). Start a local server (with PostgreSQL, Redis and RustFS) on
`http://127.0.0.1:5000`:

```sh
git clone https://github.com/EntexInteractive/Vertex.git
cd Vertex
docker compose up -d --build
```

Install `vx` on Linux and macOS (on Windows, download `vx-win-x64.zip` from
[Releases](https://github.com/EntexInteractive/Vertex/releases), or [build it](docs/getting-started.md#2-install-vx)):

```sh
curl -fsSL https://raw.githubusercontent.com/EntexInteractive/Vertex/main/install.sh | bash
```

Then:

```sh
vx config server http://127.0.0.1:5000
vx login -u admin    # the compose stack's first admin; the password is VX_ADMIN_PASSWORD (default: vertexadmin)
cd path/to/your/project
vx init              # creates a project named after the folder and makes this folder its workspace
vx status            # lists files changed since the last submit or sync
vx submit -m "First" # submits every change as one revision (#1)
vx sync              # gets what others submitted
```

To get an existing project into a folder, run `vx clone <project>` inside it. Run `vx` with no command to keep a
session open that locks files as you save them.

## Documentation

The full documentation is in [`docs/`](docs/README.md):

- **Using Vertex:** [Getting started](docs/getting-started.md), [Concepts](docs/concepts.md),
  [Command reference](docs/commands.md), [File locking](docs/locking.md),
  [Branching and merging](docs/branching-and-merging.md), [Ignore rules](docs/ignore-rules.md),
  [Users and permissions](docs/permissions.md), [Best practices](docs/best-practices.md)
- **Running a server:** [Deploying](docs/server/deployment.md), [Configuration](docs/server/configuration.md),
  [Monitoring](docs/server/monitoring.md), [Performance tuning](docs/server/performance.md)
- **Tools:** [Benchmarking with vx-bench](docs/tools/benchmarking.md),
  [Mirroring to and from git or Perforce](docs/tools/mirroring.md)
- **How Vertex works:** [Architecture](docs/internals/architecture.md), [Storage](docs/internals/storage.md),
  [Workspaces](docs/internals/workspaces.md), [History and revisions](docs/internals/history.md),
  [Events](docs/internals/events.md), [Caching](docs/internals/caching.md)

## Contributing

Contributions are welcome: bug reports, ideas, docs, and code. Read [CONTRIBUTING.md](CONTRIBUTING.md) to get started, and follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report security issues privately as described in [SECURITY.md](SECURITY.md), not in public issues.

## Maintainers

Vertex is primarily maintained by Kyle Ebbinga ([@TheGuitarleader](https://github.com/TheGuitarleader)).

## License

Vertex is free and open-source software, licensed under the
[GNU Affero General Public License v3.0](LICENSE). You may use, modify, and share it, including for commercial purposes. If you
distribute a modified version, or run one as a network service, you must make its source available under the same
license.
