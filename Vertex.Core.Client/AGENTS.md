# Vertex.Core.Client

Client-side workspace logic used by the CLI (and later by the agent and GUI). See the root AGENTS.md for project-wide
rules.

- `Workspaces/VWorkspace.cs`: a workspace is any folder with a `.vx/` directory. It holds `config`
  (`WorkspaceConfig`: server address, project, branch, user) and `index` (`WorkspaceIndex`, a SQLite database).
  Workspaces hold files, never history. `VWorkspace.Find` walks up from a path to the workspace root, the way git
  does, and `FindRequired` throws an error that tells the user to run `vx init`.
- `Workspaces/WorkspaceIndex.cs`: the local index of known files (path, size, last write, content id, base id). The
  schema is versioned with `PRAGMA user_version`. When you change it, bump `SchemaVersion` and upgrade existing
  workspaces in place; don't make users re-create them.
- `Workspaces/WorkspaceScanner.cs`: finds changes by comparing the file system with the index. It trusts the stat
  cache (size + last write time, with a racy-write window) and re-hashes only files whose stats changed. This is what
  keeps `vx status` under half a second for 100,000 unchanged files, so don't regress it.
- `IO/IgnoreRules.cs`: `.vxignore` matching with gitignore syntax. Ignoring a file that is already tracked doesn't
  delete it from the server, the same as git.
- `IO/IgnoreTemplates/`: starter `.vxignore` files that `vx init` writes when it detects a project type. Add one by
  subclassing `IgnoreTemplate`; templates are discovered automatically.
- `Database/`: SQLite connection helpers.
- `Auth/CredentialStore.cs`: logins from `vx login`, one per server, in `~/.vertex/credentials` (`VX_CONFIG_DIR`
  overrides the folder). Tokens are DPAPI-encrypted on Windows; elsewhere the file is mode 600. Never log tokens.
- `Config/UserConfig.cs`: per-user settings from `vx config` in `config.json` next to the credentials file. Today
  only `Server`, the default server used when neither `--server` nor the workspace names one.

Measure performance changes on a large tree (100,000+ files) and report the timings.

Build: `dotnet build Vertex.Core.Client/Vertex.Core.Client.csproj` from the repository root.
