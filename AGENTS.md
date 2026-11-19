# Vertex

Vertex is centralized source control for games and other big-file projects: Perforce's model (central linear
history, file locking, large binaries), git's ease of use (`vx init`, `vx status`, `vx submit`, a `.vx/` folder per
workspace, no client specs), and a stateless server that scales out (PostgreSQL metadata, Redis cache, S3 content).
The project is pre-alpha. Read README.md and `docs/` for the user-facing picture (`docs/internals/` explains the inner systems) and
CONTRIBUTING.md for the workflow.

## Priorities

Weigh every change against these, in order:

1. **Ease of use.** Commands do the obvious thing without flags, operate on the current folder, are safe to re-run,
   and error messages name the exact command that fixes the problem. When a choice trades user friction for simpler
   code, pick less friction and say so.
2. **Performance.** Treat it as a requirement. Avoid work rather than speed it up: stat cache over re-hashing, chunk
   dedup over re-uploading, one directory listing over per-file calls, batched RPCs over chatty ones. Measure with
   realistic sizes (100,000+ files, multi-GB assets) and report the numbers.
3. **Centralized model.** Workspaces hold files, never history. History lives in PostgreSQL and S3 on the server.

## Layout

| Project              | Role                                                                                      |
|----------------------|-------------------------------------------------------------------------------------------|
| `Vertex.Server`      | ASP.NET Core server. REST controllers + gRPC services, PostgreSQL, Redis, S3 chunk store. |
| `Vertex.Cli`         | The `vx` client (Spectre.Console.Cli).                                                    |
| `Vertex.Core`        | Shared by all executables: models, client interfaces, chunking (FastCDC) and zstd codec.  |
| `Vertex.Core.Client` | Client-side workspaces: `.vx/` config, SQLite index, scanner, `.vxignore` rules.          |
| `Vertex.Agent`       | Background device agent (Windows Service / systemd).                                      |
| `Vertex.Web`         | React + TypeScript + Vite UI, served by the server and embedded in the GUI.               |
| `Vertex.Gui`         | Photino desktop host for the web UI.                                                      |
| `Vertex.Tests`       | `vx-bench`: load benchmarks that drive a real deployment through the client interfaces.    |
| `Vertex.Mirror`      | `vx-mirror`: `pull` replays git or p4 into Vertex (and verifies it), `push` mirrors to git or p4. |

Each project has its own AGENTS.md with details. Read it before changing that project.

## Build and run

```sh
dotnet build Vertex.sln                                  # everything; also builds Vertex.Web via Vertex.React.targets
docker compose up -d --build                             # full local stack: server on http://127.0.0.1:5000
docker compose up -d postgres redis rustfs rustfs-init   # dependencies only, to run the server from an IDE
```

The SDK is .NET 10 (pinned in `global.json`). Building `Vertex.Server` or `Vertex.Gui` runs `npm ci` and
`npm run build` in `Vertex.Web`, so Node.js must be installed. There are no unit tests yet: verify changes by
building and by running the affected `vx` commands against the local compose stack. For anything on a hot path, also
run `dotnet run -c Release --project Vertex.Tests -- all --profile smoke` against it (see `Vertex.Tests/AGENTS.md`).

## Cross-cutting rules

- **Terminology:** the server-side history unit is a **revision** (`#1234`), never a "changeset" or "commit", in code,
  tables, RPCs, CLI output and docs.
- **Wire protocol:** clients always use **gRPC-Web** (`BaseInterface` wraps `GrpcWebHandler`), because Cloudflare
  Tunnel strips HTTP/2 trailers. gRPC-Web can't stream client to server, so uploads are unary batches (`UploadChunks`,
  at most 4 MB raw per call; see `FileTransferLimits`). Don't add client-streaming RPCs.
- **Contracts** live in `Vertex.Server/Protos/`: `Files.proto` for files, revisions and locks, `Events.proto` for the
  project event stream. `Vertex.Core` generates both client and server code from them. After changing one, build the
  whole solution.
- **Events:** every state change other parts may react to (locks, revisions, later builds) appends a project event
  with `EventStore.AppendAsync` (or `CreateAppendCommand` in a batch) in the same transaction as the change, then calls `INotificationService.NotifyAsync`
  after commit. Clients get events pushed over `EventFeed.Subscribe`; server-side reactions (build triggers, plugins)
  are `IEventHandler`s. Don't add per-feature polling or journals.
- **Server config** comes only from `VX_*` environment variables, read and validated in
  `Vertex.Server/ServerSettings.cs`. Add new settings there and document them in `docs/server/configuration.md`.
- **Schema:** until the first release, append changes to `Vertex.Server/Database/Migrations/0001_initial.sql` using
  idempotent statements only (`CREATE ... IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`), so there is one file to
  apply between releases; existing databases re-run it by hand (see CONTRIBUTING.md). After the first release, every
  change is a new migration named `NNNN_name.sql` (next number, lowercase name), and merged migrations are never
  edited or renumbered. The server applies pending migrations at startup. Timestamps are `BIGINT` Unix milliseconds.
- **Docs:** user-facing documentation lives in `docs/` (README.md stays a short landing page). A change to a command,
  option, setting, metric or user-visible behavior updates the matching page in the same change: `docs/commands.md`
  (one section per command, in the existing Parameter / Short hand / Type / Required / Description table style),
  `docs/server/configuration.md`, `docs/server/monitoring.md`, or the relevant `docs/internals/` page.
- **Secrets:** never commit credentials, connection strings with real passwords, certificates or keys.
  `launchSettings.json`, `.env` and `.vx/` are git-ignored on purpose. The values in `docker-compose.yml` and
  `.env.example` are throwaway development defaults.
- **Style:** C# uses block-scoped namespaces, braces on new lines, explicit types, `_camelCase` private fields and a
  `// Copyright 2026 Entex Interactive` header on every file. Match the surrounding code.
- **Branches:** `main` is protected; never commit or push to it directly. Do work on a branch from `main` named
  `feature/<name>` (new functionality) or `patch/<name>` (fixes), and merge it through a pull request into `main`.
  Dependabot opens its pull requests against `main` too.
- **License:** the project is AGPL-3.0. Don't add dependencies with incompatible licenses (e.g. proprietary or
  SSPL); MIT, BSD, Apache-2.0, LGPL and (A)GPL are fine.
