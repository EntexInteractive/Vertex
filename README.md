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

You need [Docker](https://docs.docker.com/get-docker/) and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
git clone https://github.com/EntexInteractive/Vertex.git
cd Vertex
docker compose up -d --build          # server + PostgreSQL + Redis + RustFS on http://127.0.0.1:5000
dotnet build Vertex.Cli -c Release    # builds the vx command line client
```

Add `Vertex.Cli/bin/Release/net10.0` to your `PATH` (or call `vx` by its full path). Or, on Linux and macOS, install
the latest released `vx` instead of building it:

```sh
curl -fsSL https://raw.githubusercontent.com/EntexInteractive/Vertex/main/install.sh | bash
```

Run it again to update. Pass options after `bash -s --`, e.g. `| bash -s -- --version 0.1.0-alpha.2`; `--uninstall`
removes it. On Windows, download `vx-win-x64.zip` from [Releases](https://github.com/EntexInteractive/Vertex/releases).

Then:

```sh
vx config server <address>   # the default server for every command on this machine (can be bypassed via `--server <url>` with most commands)
vx login -u admin    # the compose stack's first admin; the password is VX_ADMIN_PASSWORD (default: vertexadmin)
cd path/to/your/project
vx init              # creates a project named after the folder and makes this folder its workspace
vx status            # lists files changed since the last push
vx push              # uploads new and changed content
vx info              # shows the server version and latency
```

To link another folder to an existing project, run `vx fetch <project>` inside it. Run `vx --help` for every command.

## Commands

| Command                       | What it does                                                                     |
|-------------------------------|----------------------------------------------------------------------------------|
| `vx init [--template <name>]` | Creates a project on the server and makes the current folder its workspace.      |
| `vx fetch <project> [path]`   | Links a folder to an existing project.                                           |
| `vx status [--limit <n>]`     | Shows files changed since the last push.                                         |
| `vx push [paths]`             | Uploads new and changed files. Only chunks the server doesn't have are sent.     |
| `vx pull <file-id> [dest]`    | Downloads a single file by id.                                                   |
| `vx branch`                   | Shows the current branch. `list`, `<name>` and `-d <name>` list, create, delete. |
| `vx info`                     | Shows server information and who you're logged in as.                           |
| `vx login [server]`           | Logs in and saves the login in `~/.vertex/credentials`. `-u` and `--password-stdin` for scripts. |
| `vx logout [server]`          | Revokes the saved login and forgets it.                                          |
| `vx config [server [<url>]]`  | Shows or sets the default server for this machine. `--unset` removes it.         |
| `vx passwd`                   | Changes your password and logs out your other devices.                          |
| `vx access`                   | Shows who can access the project. `grant <user\|group> <read\|write\|admin>`, `revoke`, `visibility <internal\|private>`. |
| `vx user`                     | Server admins: `add`, `list`, `disable`, `enable` and `admin` users.             |
| `vx group`                    | Server admins: `create`, `delete`, `list`, `add` and `remove` group members.     |
| `vx submit`, `vx sync`        | Create a revision and update a workspace to a branch's latest state.             |

`vx init` writes a starter `.vxignore` when it recognizes the project type (currently Unreal Engine).

### Users and permissions

Every command except `vx info` and `vx login` needs a login. Permissions are deliberately simple:

- **Levels**: each project grants users or groups `read` (sync, view history), `write` (submit, lock) or `admin`
  (manage access and branches). A user's level is the highest of their own grant and their groups' grants.
- **Groups** make large teams manageable: grant a group once, then add and remove people from it.
- **Visibility**: new projects are `internal`, so every logged-in user can read them. Make one `private` with
  `vx access visibility private` to limit it to its grants.
- **Server admins** manage users and groups and have admin access to every project. Anyone can create a project and
  becomes its admin.

User and group names share one namespace, so `vx access grant Artists write` works without saying which it is.

## Architecture

| Project              | Description                                                                  |
|----------------------|------------------------------------------------------------------------------|
| `Vertex.Server`      | ASP.NET Core server: REST and gRPC endpoints, PostgreSQL, Redis, S3 storage. |
| `Vertex.Cli`         | The `vx` command line client.                                                |
| `Vertex.Core`        | Shared models, gRPC client interfaces, chunking and compression.             |
| `Vertex.Core.Client` | Workspaces (`.vx/`), the local index, scanning and ignore rules.             |
| `Vertex.Agent`       | Background device agent (Windows Service or systemd).                        |
| `Vertex.Web`         | React + Vite web UI, served by the server and embedded in the desktop app.   |
| `Vertex.Gui`         | Desktop app (Photino) that hosts the web UI.                                 |

The client always talks gRPC-Web, so Vertex works behind proxies that strip HTTP/2 trailers, such as Cloudflare Tunnel. The server accepts both gRPC-Web and native gRPC.

## Running the server

Server images for `linux/amd64` and `linux/arm64` are published to the GitHub Container Registry and Docker Hub:

| Tag                 | What it is                                              |
|---------------------|---------------------------------------------------------|
| `stable`            | The latest release. Use this for real deployments.      |
| `1.2.0`             | A specific release.                                     |
| `latest`            | The latest build of `main`. May be unstable.           |

```sh
docker pull ghcr.io/entexinteractive/vertex-server:stable
```

### Without Docker

Every [release](https://github.com/EntexInteractive/Vertex/releases/latest) also includes a self-contained server
for Windows, Linux and macOS (`VertexServer-<platform>`, e.g. `VertexServer-linux-x64.tar.gz`), with no .NET install needed. Extract it, set the environment
variables below, and run `VertexServer`. You still need PostgreSQL and S3-compatible storage; Redis is optional.
The server creates its database schema on first start and upgrades it automatically when you update the server.

`docker-compose.yml` starts a complete local stack with development credentials. To change them, copy `.env.example` to `.env`. To run the server from your IDE instead, start only the dependencies:

```sh
docker compose up -d postgres redis rustfs rustfs-init
```

Then set the variables below in `Vertex.Server/Properties/launchSettings.json` (git-ignored) or with `dotnet user-secrets`.

### Configuration

The server is configured entirely through environment variables. At startup, it validates them and exits with a list of every missing or invalid setting.

| Variable                 | Required | Default                    | Description                                                                                              |
|--------------------------|----------|----------------------------|----------------------------------------------------------------------------------------------------------|
| `VX_HTTP_PORT`           | No       | `5000`                     | Port the server listens on. Clear-text HTTP/2 only, unless TLS is configured below.                      |
| `VX_POSTGRES_CONNECTION` | **Yes**  | —                          | Npgsql connection string, e.g. `Host=postgres;Port=5432;Database=vertex;Username=...;Password=...`.      |
| `VX_REDIS_CONNECTION`    | No       | `redis:6379`               | StackExchange.Redis connection string. The server uses a memory-only cache when Redis is unreachable. |
| `VX_S3_ENDPOINT`         | No       | `https://s3.amazonaws.com` | S3 or S3-compatible endpoint URL (e.g. MinIO, RustFS). Path-style addressing is used.                    |
| `VX_S3_REGION`           | No       | `us-east-1`                | S3 region.                                                                                               |
| `VX_S3_BUCKET`           | No       | `vertex-storage`           | Bucket for file chunks and manifests.                                                                    |
| `VX_S3_ACCESS_KEY`       | No       | —                          | Static S3 access key. Set together with `VX_S3_SECRET_KEY`, or leave both unset.                         |
| `VX_S3_SECRET_KEY`       | No       | —                          | Static S3 secret key.                                                                                    |
| `VX_TLS_CERT`            | No       | —                          | PEM certificate file. With `VX_TLS_KEY`, enables TLS and serves both HTTP/1.1 and HTTP/2 on the port.    |
| `VX_TLS_KEY`             | No       | —                          | PEM private key file for `VX_TLS_CERT`.                                                                  |
| `VX_ADMIN_PASSWORD`      | No       | —                          | Creates the `admin` server admin on first start, while no users exist. Ignored afterwards. At least 8 characters. |
| `VX_TRUSTED_PROXIES`     | No       | —                          | Comma-separated addresses or CIDR ranges of proxies allowed to set `X-Forwarded-For`, e.g. `172.18.0.0/16`. Loopback is always trusted. |

When the S3 keys are unset, the AWS SDK default credential chain is used (`AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`, EKS IRSA or pod identity, instance profile). In Kubernetes, prefer workload identity, or load the keys from a Secret. Standard ASP.NET Core variables such as `ASPNETCORE_ENVIRONMENT` also apply; `Development` enables Swagger and gRPC reflection.

### Behind a reverse proxy

Without TLS, the server speaks only clear-text HTTP/2 (h2c), which most proxies can't forward. When the server sits
behind a proxy or tunnel such as Cloudflare Tunnel, set `VX_TLS_CERT` and `VX_TLS_KEY` (an origin certificate is fine)
and have the proxy connect over HTTPS with HTTP/2 enabled.

Also set `VX_TRUSTED_PROXIES` to the proxy's address, unless it runs on the same machine (loopback is trusted). The
server limits failed logins per client address, and without it every client behind the proxy shares one address.

## Contributing

Contributions are welcome: bug reports, ideas, docs, and code. Read [CONTRIBUTING.md](CONTRIBUTING.md) to get started, and follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report security issues privately as described in [SECURITY.md](SECURITY.md), not in public issues.

## Maintainers

Vertex is primarily maintained by Kyle Ebbinga ([@TheGuitarleader](https://github.com/TheGuitarleader)).

## License

Vertex is free and open-source software, licensed under the
[GNU Affero General Public License v3.0](LICENSE). You may use, modify, and share it, including for commercial purposes. If you
distribute a modified version, or run one as a network service, you must make its source available under the same
license.
