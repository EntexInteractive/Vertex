# Vertex.Core.Client

Client-side workspace logic used by the CLI (and later by the agent and GUI). See the root AGENTS.md for project-wide
rules.

- `Workspaces/VWorkspace.cs`: a workspace is any folder with a `.vx/` directory. It holds `config`
  (`WorkspaceConfig`: server address, project, branch, user) and `index` (`WorkspaceIndex`, a SQLite database).
  Workspaces hold files, never history. `VWorkspace.Find` walks up from a path to the workspace root, the way git
  does, and `FindRequired` throws an error that tells the user to run `vx init`.
- `Workspaces/WorkspaceIndex.cs`: the local index of known files (path, size, last write, content id, base id), a
  `meta` table (synced revision, lock seq) and a `locks` table mirroring the branch's locks. The
  schema is versioned with `PRAGMA user_version`. When you change it, bump `SchemaVersion` and upgrade existing
  workspaces in place; don't make users re-create them.
- `Workspaces/WorkspaceScanner.cs`: finds changes by comparing the file system with the index. It trusts the stat
  cache (size + last write time, with a racy-write window) and re-hashes only files whose stats changed. This is what
  keeps `vx status` under half a second for 100,000 unchanged files, so don't regress it.
- `Workspaces/WorkspaceLocks.cs`: the lock step every workspace command runs after scanning. It locks changed files,
  releases locks on files no longer changed, applies everyone's lock events since the stored seq, and flips the
  read-only flag only on files whose lock changed (never on a file changed locally, and it only clears flags it set
  itself). It has no CLI dependency so every host can drive it.
- `Workspaces/WorkspaceMonitor.cs`: live locking for a host that stays open (the interactive `vx` session today, the
  GUI later). A `FileSystemWatcher` records touched paths; after 500 ms of quiet it locks them on stats alone
  (`WorkspaceScanner.ScanPaths(..., hash: false)`), then hashes them and releases the ones that didn't really change,
  so a multi-GB save or a burst of thousands of files is locked in about a second. Other people's changes arrive on
  the project event stream (`EventsInterface`), applied with `WorkspaceLocks.ApplyStreamed` and resumed from the
  stored `event_seq` after any disconnect (back-off 1-60 s); an older server without the stream is polled every 60 s.
  It re-reads the login token when the old one is revoked and raises events (`LockConflict`, `Activity`,
  `ProblemChanged`) instead of printing; activity is held for 1 s so a touch (lock, then release) isn't reported.
  Hosts must `PauseAsync`/`Resume` it around their own scans so the index has one writer.
- `Workspaces/WorkspaceScanner.ScanPaths`: checks only some paths (files or folders) with the same per-file logic as
  `Scan`, using the index's case-insensitive path lookups (`WorkspaceIndex.Find`) instead of reading every row.
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
