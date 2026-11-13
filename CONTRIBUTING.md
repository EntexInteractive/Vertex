# Contributing to Vertex

Thanks for your interest in Vertex! You can help by reporting bugs, suggesting ideas, improving docs and writing code.
Everyone who takes part agrees to the [Code of Conduct](CODE_OF_CONDUCT.md).

Vertex is pre-alpha and moves fast, so **open an issue before starting anything large.** That way we can agree on the
approach before you spend time on it.

## What we care about

These priorities decide most design questions, so keep them in mind for any change:

1. **Ease of use comes first.** Vertex should never fight its user. Commands do the obvious thing without flags, work
   on the current folder, can be run again safely, and when something goes wrong, the error says exactly which command
   fixes it.
2. **Performance is a requirement, not a later optimization.** Prefer designs that skip work entirely, such as stat
   caches over re-hashing, dedup over re-uploading, and one directory listing over a call per file. Test with realistic
   sizes (100,000+ files, multi-gigabyte assets) and include numbers in your PR.
3. **Perforce's model, git's feel.** History is central and linear and lives on the server; workspaces hold files,
   not history. The server is stateless and scales out.

## Reporting bugs and requesting features

- **Bugs:** open an issue using the bug report template. Include your OS, the Vertex version (`vx info`), the exact
  command, and what you expected to happen. Run the command with `-log` for debug output.
- **Ideas:** open an issue using the feature request template. Describe the problem you're trying to solve before
  the solution.
- **Security vulnerabilities:** **don't** open a public issue. See [SECURITY.md](SECURITY.md).

## Development setup

You need:

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`)
- [Node.js](https://nodejs.org/) 22 LTS or later. Building the server or desktop app also builds `Vertex.Web`.
- [Docker](https://docs.docker.com/get-docker/) for PostgreSQL, Redis and S3-compatible storage

```sh
git clone https://github.com/EntexInteractive/Vertex.git
cd Vertex
docker compose up -d postgres redis rustfs rustfs-init   # dependencies only
dotnet build Vertex.sln
```

To run the server from your IDE, set the environment variables listed in the [README](README.md#configuration) in
`Vertex.Server/Properties/launchSettings.json`, which is git-ignored. Use these values for the compose stack:

```text
VX_POSTGRES_CONNECTION = Host=127.0.0.1;Port=5432;Database=vertex;Username=vertex;Password=vertex
VX_REDIS_CONNECTION    = 127.0.0.1:6379
VX_S3_ENDPOINT         = http://127.0.0.1:9000
VX_S3_ACCESS_KEY       = vertexadmin
VX_S3_SECRET_KEY       = vertexadmin
ASPNETCORE_ENVIRONMENT = Development
```

To run the whole stack in containers instead, run `docker compose up -d --build`.

The database schema lives in `Vertex.Server/Database/schema.sql` and must stay idempotent, because it runs against
existing databases. The compose Postgres container only applies it when its volume is first created, so reset with
`docker compose down -v` after changing it.

### Project layout

| Project              | What lives there                                                   |
|----------------------|--------------------------------------------------------------------|
| `Vertex.Server`      | ASP.NET Core server, gRPC services, PostgreSQL, Redis, S3 storage. |
| `Vertex.Cli`         | The `vx` command line client (Spectre.Console).                    |
| `Vertex.Core`        | Shared models, gRPC client interfaces, chunking and compression.   |
| `Vertex.Core.Client` | Workspaces, the local SQLite index, scanning and ignore rules.     |
| `Vertex.Agent`       | Background device agent.                                           |
| `Vertex.Web`         | React + TypeScript + Vite web UI.                                  |
| `Vertex.Gui`         | Photino desktop app hosting the web UI.                            |

The gRPC contract is `Vertex.Server/Protos/Files.proto`. The server and clients generate code from it, so build the
whole solution after changing it.

## Making changes

1. Fork the repository and create a branch from **`develop`**. `main` holds stable releases; pull requests go to
   `develop`.
2. Keep each pull request focused on one change. Separate refactors from behavior changes.
3. Follow the existing style:
    - C#: block-scoped namespaces, braces on new lines, `PascalCase` public members, `_camelCase` private fields,
      explicit types where the codebase uses them, and a `// Copyright 2026 Entex Interactive` header in new files.
    - TypeScript: run `npm run lint` in `Vertex.Web`.
    - The server reads configuration only from environment variables, in `ServerSettings.cs`. Add new settings there
      and document them in the README.
    - Call the server-side history unit a **revision**, never a "changeset", in code, CLI output and docs.
4. Make sure everything builds: `dotnet build Vertex.sln`, and for web changes, `npm run lint` and `npm run build` in
   `Vertex.Web`.
5. Run the commands you touched against a local server, and describe what you tested in the pull request.
6. Write commit messages in the imperative mood, with a short summary line (for example, "Add branch delete
   confirmation").

### Things that must never be committed

Passwords, connection strings with real credentials, API keys, TLS certificates and private keys, `.env` files, and
`launchSettings.json`. Configuration comes from environment variables. If you commit a secret by accident, tell us
right away so we can rotate it. Deleting the file in a later commit doesn't remove it from history.

## Licensing of contributions

Vertex is licensed under the [GNU AGPL-3.0](LICENSE). By submitting a contribution, you agree that it is licensed
under the same license (inbound = outbound), and that you have the right to submit it. There is no CLA to sign.
