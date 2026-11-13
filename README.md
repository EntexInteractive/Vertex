# [<img src="https://raw.githubusercontent.com/EntexInteractive/Vertex/master/Vertex.Cli/vertex-icon.ico" alt="Vertex Icon" width="32" height="32">](https://github.com/EntexInteractive/Vertex) Vertex

[![.NET](https://img.shields.io/github/actions/workflow/status/EntexInteractive/Vertex/build-latest.yml?label=Main%20build&style=for-the-badge)](https://github.com/EntexInteractive/Vertex/actions/workflows/build-latest.yml) [![latest version](https://img.shields.io/github/v/release/EntexInteractive/Vertex?label=Latest%20release&style=for-the-badge)](https://github.com/EntexInteractive/Vertex/releases/latest) [![GitHub Downloads](https://img.shields.io/github/downloads/EntexInteractive/Vertex/total?style=for-the-badge)](https://github.com/EntexInteractive/Vertex/releases/latest) [![Docker Pulls](https://img.shields.io/docker/pulls/EntexInteractive/Vertex?style=for-the-badge)
]()

**Version control without the hassle.**

Vertex keeps a single central history on a server, so large binary assets don't live in every clone, and the command line feels like git: `vx init`, `vx status`, `vx push`. The server is stateless and stores file content in S3, so you can run it on a single Docker host or scale it out in Kubernetes.

> [!WARNING]
> Vertex is **pre-alpha**. Workspaces, branches, status, and content upload work, but revisions (`vx submit`) and syncing (`vx sync`) are still being built. Don't trust it with your only copy of anything yet.

## Why Vertex?

- **Made for big binary files.** Files are split with content-defined chunking (FastCDC) and compressed with zstd, so
  An edited multi-gigabyte asset uploads only the changed chunks, and identical content is stored once.
- **Centralized, like Perforce.** History lives on the server, not in every workspace, and revisions are numbered (`#1234`) and linear. A workspace holds only the files you work on.
- **Simple, like git.** No client specs or workspace mappings. A `.vx/` folder makes any folder a workspace, and `.vxignore` uses gitignore syntax.
- **Fast.** `vx status` uses a stat cache and checks 100,000 unchanged files in under half a second.
- **Scales out.** The server is stateless. It uses PostgreSQL for metadata, Redis for caching, and any S3-compatible store (AWS S3, MinIO, RustFS) for content.

The long-term goal is a single product for source control, CI, and issue tracking, rather than stitching together Perforce + TeamCity + Jira.

## Quick start

You need [Docker](https://docs.docker.com/get-docker/) and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
git clone https://github.com/EntexInteractive/Vertex.git
cd Vertex
docker compose up -d --build          # server + PostgreSQL + Redis + RustFS on http://127.0.0.1:5000
dotnet build Vertex.Cli -c Release    # builds the vx command line client
```

Add `Vertex.Cli/bin/Release/net10.0` to your `PATH` (or call `vx` by its full path), then:

```sh
cd path/to/your/project
vx init              # creates a project named after the folder and makes this folder its workspace
vx status            # lists files changed since the last push
vx push              # uploads new and changed content
vx info              # shows the server version and latency
```

To link another folder to an existing project, run `vx fetch <project>` inside it. Run `vx --help` for every command. Commands use `http://127.0.0.1:5000` by default; pass `--server <url>` to use another server.

## Commands

| Command                       | What it does                                                                     |
|-------------------------------|----------------------------------------------------------------------------------|
| `vx init [--template <name>]` | Creates a project on the server and makes the current folder its workspace.      |
| `vx fetch <project> [path]`   | Links a folder to an existing project.                                           |
| `vx status [--limit <n>]`     | Shows files changed since the last push.                                         |
| `vx push [paths]`             | Uploads new and changed files. Only chunks the server doesn't have are sent.     |
| `vx pull <file-id> [dest]`    | Downloads a single file by id.                                                   |
| `vx branch`                   | Shows the current branch. `list`, `<name>` and `-d <name>` list, create, delete. |
| `vx info`                     | Shows server information.                                                        |
| `vx submit`, `vx sync`        | Create a revision and update a workspace to a branch's latest state.             |

`vx init` writes a starter `.vxignore` when it recognizes the project type (currently Unreal Engine).

## Architecture

```
 vx (CLI) ─┐                       ┌─► PostgreSQL   projects, branches, revisions
 Agent  ───┼─ gRPC / gRPC-Web ─►  Vertex.Server ─┼─► Redis        cache (optional)
 Web UI ───┘                       └─► S3           chunks and file manifests
```

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

When the S3 keys are unset, the AWS SDK default credential chain is used (`AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`, EKS IRSA or pod identity, instance profile). In Kubernetes, prefer workload identity, or load the keys from a Secret. Standard ASP.NET Core variables such as `ASPNETCORE_ENVIRONMENT` also apply; `Development` enables Swagger and gRPC reflection.

### Behind a reverse proxy

Without TLS, the server speaks only clear-text HTTP/2 (h2c), which most proxies can't forward. When the server sits
behind a proxy or tunnel such as Cloudflare Tunnel, set `VX_TLS_CERT` and `VX_TLS_KEY` (an origin certificate is fine)
and have the proxy connect over HTTPS with HTTP/2 enabled.

## Contributing

Contributions are welcome: bug reports, ideas, docs, and code. Read [CONTRIBUTING.md](CONTRIBUTING.md) to get started, and follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report security issues privately as described in [SECURITY.md](SECURITY.md), not in public issues.

## Maintainers

Vertex is primarily maintained by Kyle Ebbinga ([@TheGuitarleader](https://github.com/TheGuitarleader)) (Entex Interactive).

## License

Vertex is free and open-source software, licensed under the
[GNU Affero General Public License v3.0](LICENSE). You may use, modify, and share it, including for commercial purposes. If you
distribute a modified version, or run one as a network service, you must make its source available under the same
license.
