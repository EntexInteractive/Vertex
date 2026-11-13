# Vertex

Vertex is centralized source control for games and other big-file projects: Perforce's model (central linear
history, file locking, large binaries), git's ease of use (`vx init`, `vx status`, `vx push`, a `.vx/` folder per
workspace, no client specs), and a stateless server that scales out (PostgreSQL metadata, Redis cache, S3 content).
The project is pre-alpha. Read README.md for the user-facing picture and CONTRIBUTING.md for the workflow.

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

Each project has its own AGENTS.md with details. Read it before changing that project.

## Build and run

```sh
dotnet build Vertex.sln                                  # everything; also builds Vertex.Web via Vertex.React.targets
docker compose up -d --build                             # full local stack: server on http://127.0.0.1:5000
docker compose up -d postgres redis rustfs rustfs-init   # dependencies only, to run the server from an IDE
```

The SDK is .NET 10 (pinned in `global.json`). Building `Vertex.Server` or `Vertex.Gui` runs `npm ci` and
`npm run build` in `Vertex.Web`, so Node.js must be installed. There is no test project yet: verify changes by
building and by running the affected `vx` commands against the local compose stack.

## Cross-cutting rules

- **Terminology:** the server-side history unit is a **revision** (`#1234`), never a "changeset" or "commit", in code,
  tables, RPCs, CLI output and docs.
- **Wire protocol:** clients always use **gRPC-Web** (`BaseInterface` wraps `GrpcWebHandler`), because Cloudflare
  Tunnel strips HTTP/2 trailers. gRPC-Web can't stream client to server, so uploads are unary batches (`UploadChunks`,
  at most 4 MB raw per call; see `FileTransferLimits`). Don't add client-streaming RPCs.
- **Contracts:** `Vertex.Server/Protos/Files.proto` is the single source. `Vertex.Core` generates both client and
  server code from it. After changing it, build the whole solution.
- **Server config** comes only from `VX_*` environment variables, read and validated in
  `Vertex.Server/ServerSettings.cs`. Add new settings there and document them in the README table.
- **Schema:** `Vertex.Server/Database/schema.sql` must be idempotent (`IF NOT EXISTS`), with tables created in
  dependency order. Timestamps are `BIGINT` Unix milliseconds.
- **Secrets:** never commit credentials, connection strings with real passwords, certificates or keys.
  `launchSettings.json`, `.env` and `.vx/` are git-ignored on purpose. The values in `docker-compose.yml` and
  `.env.example` are throwaway development defaults.
- **Style:** C# uses block-scoped namespaces, braces on new lines, explicit types, `_camelCase` private fields and a
  `// Copyright 2026 Entex Interactive` header on every file. Match the surrounding code.
- **Branches:** work targets `develop`; `main` is stable. Dependabot also targets `develop`.
- **License:** the project is AGPL-3.0. Don't add dependencies with incompatible licenses (e.g. proprietary or
  SSPL); MIT, BSD, Apache-2.0, LGPL and (A)GPL are fine.
