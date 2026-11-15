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
vx status            # lists files changed since the last submit or sync
vx submit -m "First" # submits every change as one revision (#1)
vx sync              # gets what others submitted
vx info              # shows the server version and latency
```

To link another folder to an existing project, run `vx fetch <project>` inside it. Run `vx --help` for every command.

## Commands

| Command                       | What it does                                                                     |
|-------------------------------|----------------------------------------------------------------------------------|
| `vx init [--template <name>]` | Creates a project on the server and makes the current folder its workspace.      |
| `vx fetch <project> [path]`   | Links a folder to an existing project.                                           |
| `vx status [--limit <n>]`     | Shows files changed since the last submit or sync.                               |
| `vx submit -m <msg> [paths]`  | Submits every change (or those under `paths`) as one revision. Only chunks the server doesn't have are sent. Refused when a file changed on the server since your last sync. |
| `vx sync`                     | Gets files changed on the branch since the last sync. Local changes are never overwritten. |
| `vx revert <paths>`           | Discards local changes, restoring the last submitted or synced content from the server. |
| `vx locks [paths]`            | Lists who has which files locked on the branch, and on which machine.            |
| `vx unlock <paths>`           | Releases locks held elsewhere: yours from a workspace that's gone, or anyone's for project admins. |
| `vx branch`                   | Shows the current branch. `list`, `<name>` and `-d <name>` list, create, delete. |
| `vx info`                     | Shows server information and who you're logged in as.                           |
| `vx login [server]`           | Logs in and saves the login in `~/.vertex/credentials`. `-u` and `--password-stdin` for scripts. |
| `vx logout [server]`          | Revokes the saved login and forgets it.                                          |
| `vx config [server [<url>]]`  | Shows or sets the default server for this machine. `--unset` removes it.         |
| `vx passwd`                   | Changes your password and logs out your other devices.                          |
| `vx access`                   | Shows who can access the project. `grant <user\|group> <read\|write\|admin>`, `revoke`, `visibility <internal\|private>`. |
| `vx user`                     | Server admins: `add`, `list`, `disable`, `enable` and `admin` users.             |
| `vx group`                    | Server admins: `create`, `delete`, `list`, `add` and `remove` group members.     |

`vx init` writes a starter `.vxignore` when it recognizes the project type (currently Unreal Engine).

### File locking

There is no checkout step. While `vx` is open in a workspace (run `vx` with no command), it watches the folder and
locks each file on the branch about a second after you save it. The server pushes what others do, so their locked
files turn read-only right away and the session tells you about it:

```text
alice locked Content/Hero.uasset
alice submitted #42 "Fix jump" (3 file(s)). Run 'vx sync' to get it.
```

Without an open session, `vx status` (or `submit`, `sync`, `revert`) does the same when you run it. The
lock goes away when you submit the file, revert it, or change it back. Anyone
else's copy of a file you hold becomes read-only the next time their `vx` talks to the server, and the server refuses
to submit a file someone else holds:

```text
! Modified  Maps/City.umap  (locked by alice on DESKTOP-7; wait for them to submit it, or run 'vx revert Maps/City.umap' to drop your change)
```

Locks belong to one workspace, so the same person editing a file on two machines is warned too. `vx locks` shows who
holds what; `vx unlock` clears a lock left behind by a workspace that no longer exists. `vx status` still works
offline; it just says it couldn't update locks.

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
| `VX_POSTGRES_CONNECTION` | **Yes**  | —                          | Npgsql connection string, e.g. `Host=postgres;Port=5432;Database=vertex;Username=...;Password=...`. Pool settings go here too: `Maximum Pool Size` (default 100, per instance), `Timeout` (seconds to wait for a free connection, default 15) and `Command Timeout` (default 30). Watch `db_client_connection_*` before raising them. |
| `VX_REDIS_CONNECTION`    | No       | `redis:6379`               | StackExchange.Redis connection string. The server uses a memory-only cache when Redis is unreachable. Run Redis as a pure cache: see below. |
| `VX_S3_ENDPOINT`         | No       | `https://s3.amazonaws.com` | S3 or S3-compatible endpoint URL (e.g. MinIO, RustFS). Path-style addressing is used.                    |
| `VX_S3_REGION`           | No       | `us-east-1`                | S3 region.                                                                                               |
| `VX_S3_BUCKET`           | No       | `vertex-storage`           | Bucket for file chunks and manifests.                                                                    |
| `VX_S3_ACCESS_KEY`       | No       | —                          | Static S3 access key. Set together with `VX_S3_SECRET_KEY`, or leave both unset.                         |
| `VX_S3_SECRET_KEY`       | No       | —                          | Static S3 secret key.                                                                                    |
| `VX_TLS_CERT`            | No       | —                          | PEM certificate file. With `VX_TLS_KEY`, enables TLS and serves both HTTP/1.1 and HTTP/2 on the port.    |
| `VX_TLS_KEY`             | No       | —                          | PEM private key file for `VX_TLS_CERT`.                                                                  |
| `VX_ADMIN_PASSWORD`      | No       | —                          | Creates the `admin` server admin on first start, while no users exist. Ignored afterwards. At least 8 characters. |
| `VX_TRUSTED_PROXIES`     | No       | —                          | Comma-separated addresses or CIDR ranges of proxies allowed to set `X-Forwarded-For`, e.g. `172.18.0.0/16`. Loopback is always trusted. |
| `VX_METRICS_PORT`        | No       | —                          | Serves Prometheus metrics on `/metrics` over plain HTTP on this port (e.g. `9464`). Off when unset. Must differ from `VX_HTTP_PORT`; don't expose it publicly. |

When the S3 keys are unset, the AWS SDK default credential chain is used (`AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`, EKS IRSA or pod identity, instance profile). In Kubernetes, prefer workload identity, or load the keys from a Secret. Standard ASP.NET Core variables such as `ASPNETCORE_ENVIRONMENT` also apply; `Development` enables Swagger and gRPC reflection.

Redis only holds values the server can rebuild (cached rows, counters, cross-instance wake-ups), so run it as a pure
cache: `redis-server --save "" --appendonly no --maxmemory 256mb --maxmemory-policy allkeys-lru`, as
`docker-compose.yml` does. Snapshots and append-only rewrites fork Redis and write the whole dataset to disk, which
on a busy host stalls everything sharing that disk. Give each deployment its own Redis (or its own database number),
since cache keys aren't namespaced per server.

### Behind a reverse proxy

Without TLS, the server speaks only clear-text HTTP/2 (h2c), which most proxies can't forward. When the server sits
behind a proxy or tunnel such as Cloudflare Tunnel, set `VX_TLS_CERT` and `VX_TLS_KEY` (an origin certificate is fine)
and have the proxy connect over HTTPS with HTTP/2 enabled.

Also set `VX_TRUSTED_PROXIES` to the proxy's address, unless it runs on the same machine (loopback is trusted). The
server limits failed logins per client address, and without it every client behind the proxy shares one address.

### Monitoring

Set `VX_METRICS_PORT` and point Prometheus at it; build dashboards and alerts in Grafana. The metrics come from
OpenTelemetry and are only served on that port, never on the public one:

```yaml
scrape_configs:
  - job_name: vertex
    static_configs:
      - targets: ["vertex-server:9464"]  # every server instance
```

Besides the standard ASP.NET Core (request durations per endpoint and gRPC method, Kestrel connections), .NET runtime
and Npgsql (query durations, pool usage) metrics, the server reports its own. Durations are histograms in seconds.

| Metric | What it tells you |
|---|---|
| `vertex_project_lock_wait_seconds` (by `operation`) | Time writes wait for their project. Writes in a project run one transaction at a time; this climbing is the first sign a project is near its write limit. |
| `vertex_project_lock_held_seconds`, `vertex_db_commit_seconds` (by `operation`: locks, submit) | How long each project transaction holds the project, and how much of that is the commit (the database's durable write). A project does at most one transaction per hold time; when the commit dominates, the database's storage is the limit (see [Postgres on ZFS](#postgresql-on-zfs)). |
| `vertex_project_writes_batch` | Lock and unlock requests that shared one transaction. Concurrent requests for a project are combined, so under load one commit covers many of them. |
| `vertex_events_delivery_delay_seconds` | Time from a change to it reaching a client's stream: how "live" locks and read-only flags really are. |
| `vertex_events_delivered_total`, `vertex_events_recorded_total` (by `type`) | Push volume. Delivered ≈ recorded × subscribers, the main CPU cost. |
| `vertex_event_streams`, `vertex_event_feeds` | Open streams and projects with live subscribers, per instance. |
| `vertex_events_handler_lag` (by `handler`) | How many events a server-side handler (builds, plugins) is behind. |
| `vertex_revisions_total`, `vertex_revisions_files` (histogram), `vertex_revisions_refused_total` (by `reason`) | Submits, their size, and people blocked by out-of-date files or others' locks. |
| `vertex_locks_granted_total`, `vertex_locks_contended_total`, `vertex_locks_released_total` (by `reason`) | Locking activity and contention on shared files. |
| `vertex_sync_files_total` | Files handed out by `vx sync`. |
| `vertex_storage_duration_seconds` (by `operation`, `outcome`), `vertex_storage_cache_total` (by `result`) | S3 latency and errors (S3 holds chunk bytes; which chunks and files exist is indexed in PostgreSQL), and how often the in-memory cache answers an existence check without the database. |
| `vertex_transfer_chunks_total`, `vertex_transfer_files_total` (by `result`: new, deduplicated) | How much content clients didn't have to upload. |
| `vertex_transfer_received_bytes_total`, `vertex_transfer_sent_bytes_total` | Upload and download bandwidth. |
| `vertex_transfer_budget_wait_seconds`, `vertex_transfer_slots_used`, `vertex_transfer_slots` | Time transfers wait for a slot before touching S3, and slots in use. Slow transfers with long waits mean the instance is at its memory-based limit; slow transfers without waits mean S3 itself is slow (see `vertex_storage_duration_seconds`). |
| `vertex_cache_lookups_total`, `vertex_cache_duration_seconds` (by `category`, `result`), `vertex_cache_factory_seconds`, `vertex_cache_value_size_bytes` (by `type`) | Cache hit rate, lookup time (a hit taking milliseconds came from Redis), the cost of misses, and the size of cached values. `category="gen"` are cheap generation lookups; leave them out of hit-rate panels. |
| `vertex_grpc_errors_total` (by `method`, `status`, `exception`) | Calls that failed with an exception. `Unavailable` means overload (database pool or timeouts, S3 errors or timeouts) and is logged as a warning naming the cause; `Internal` is a server bug. Slow S3 calls (over 2 s) are also logged. |
| `vertex_redis_connected`, `vertex_redis_errors_total` | Without Redis, cross-instance push falls back to 30 s checks. |
| `vertex_auth_logins_total` (by `outcome`: success, failed, disabled, throttled) | Login activity and brute-force attempts. |

These come from the server itself. To tell whether a slow server is waiting on its disks or its database, also
scrape the hosts and the database:

- [node_exporter](https://github.com/prometheus/node_exporter) on every host running Vertex, PostgreSQL or the S3
  store: disk latency (`node_disk_write_time_seconds_total` / `node_disk_writes_completed_total`), CPU per mode
  including iowait, and on ZFS the `node_zfs_*` pool and ARC statistics.
- [postgres_exporter](https://github.com/prometheus-community/postgres_exporter), with `pg_stat_statements` loaded
  (`shared_preload_libraries`): commits, locks, wait events, WAL and checkpoint timing, and time per statement.
- [cAdvisor](https://github.com/google/cadvisor) when several services share one machine, to see which one uses the
  CPU.
- A 5 s scrape interval while benchmarking, so short spikes and ramps are visible.

#### PostgreSQL on ZFS

Every commit is a synchronous write. On ZFS without a separate log device (SLOG), that write goes to the main pool and
costs milliseconds to tens of milliseconds, and `vertex_db_commit_seconds` shows it. Writes to one project run one
transaction at a time, so that cost caps each project's write rate (Vertex combines concurrent lock requests to
soften it). To make commits fast:

- Add a SLOG on a fast SSD with power-loss protection (`zpool add <pool> log <device>`). This is the big one.
- Give PostgreSQL its own dataset: `recordsize=16K`, `compression=lz4`, `atime=off`, `logbias=latency`.
- In `postgresql.conf`: `full_page_writes = off` (safe on ZFS, which never writes a page partially),
  `wal_init_zero = off` and `wal_recycle = off`.
- Put the S3 store's data on a separate dataset (`recordsize=1M`), so its object writes don't queue with commits.

### Benchmarking a deployment

`Vertex.Tests` builds `vx-bench`, which loads a server the way real clients do (same gRPC-Web calls as `vx`) and
reports client-side latency percentiles and throughput, with each scenario's UTC window so you can line it up with the
metrics above. It uses your `vx login` for the server and creates a `bench-<time>` project per run.

```sh
vx login https://vertex.example.com
dotnet run -c Release --project Vertex.Tests -- all --server https://vertex.example.com --profile standard
dotnet run -c Release --project Vertex.Tests -- soak --server https://vertex.example.com --duration 30m
```

Scenarios: `latency`, `upload`, `dedup`, `download`, `submit` (one large first submit and sync), `submitters`
(concurrent revisions), `locks`, `events` (stream fan-out) and `soak` (all of it at once, until Ctrl+C). Profiles are
`smoke` (about 2 minutes), `standard` (2 GB of assets, 100,000 files, 16 clients) and `stress` (10 GB, 250,000 files, 64
clients); `--clients`, `--concurrency`, `--duration` and the size flags override them. Reports are saved as Markdown and
JSON in `./bench-results`. Run it from a machine outside the server's network to include the proxy, or against the
origin directly to leave it out.

## Contributing

Contributions are welcome: bug reports, ideas, docs, and code. Read [CONTRIBUTING.md](CONTRIBUTING.md) to get started, and follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report security issues privately as described in [SECURITY.md](SECURITY.md), not in public issues.

## Maintainers

Vertex is primarily maintained by Kyle Ebbinga ([@TheGuitarleader](https://github.com/TheGuitarleader)).

## License

Vertex is free and open-source software, licensed under the
[GNU Affero General Public License v3.0](LICENSE). You may use, modify, and share it, including for commercial purposes. If you
distribute a modified version, or run one as a network service, you must make its source available under the same
license.
