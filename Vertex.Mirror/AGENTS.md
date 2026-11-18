# Vertex.Mirror

`vx-mirror`, a console app that mirrors a Vertex branch to or from other version control systems, through the same
client interfaces `vx` uses. See the root AGENTS.md for project-wide rules.

```sh
vx login http://127.0.0.1:5000
# git -> Vertex, one commit every 5 minutes, checking every byte (an all-day integrity check)
dotnet run -c Release --project Vertex.Mirror -- pull --project gitignore --git https://github.com/github/gitignore.git
dotnet run -c Release --project Vertex.Mirror -- pull --project vertex-self --git D:\Git\Vertex --start 1 --interval 1m
# Vertex -> git or Perforce, every new revision as it lands
dotnet run -c Release --project Vertex.Mirror -- push --project game --git git@github.com:studio/game-mirror.git
dotnet run -c Release --project Vertex.Mirror -- push --project game --p4 game-mirror-ws --once
```

Both commands log in like `vx` (`--server`, `--token`, `VX_MIRROR_TOKEN`, then the `vx login` credential), take the
Vertex side as `--project` and `--branch` (default `main`), run until `--duration` or Ctrl+C, keep their working
copies in `--work` (default: a temp folder, deleted at the end), and write `<command>-<run>.log` under `--out`
(default `./mirror-results`). Exit code 0 means no problems, 1 problems (see the log), 2 a setup error.

- `pull` (`Pull/GitPull.cs`), git -> Vertex: clones `--git` (needs git), starts at `--start` (commit number,
  1 = first, or a sha/tag) or `--replay` commits behind its head (default 300), and every `--interval` (default 5m)
  moves one first-parent commit forward (fetching upstream once caught up). Each commit is submitted like
  `vx submit`, with the commit's raw message as `-m` and the files at the repository's own paths at the branch root.
  It then syncs the revision into a second folder like `vx sync` and checks the file set, sizes and SHA-256 of every
  downloaded file against git's copy; every `--full-check-every` cycles (default 12) it also compares the whole
  listing and re-downloads everything. One CSV row per cycle (`pull-<run>.csv`), mismatched files kept as
  `.vertex`/`.git` pairs in `pull-<run>-evidence/`. Problems are counted and the run keeps going; git fetch failures
  (offline) are logged, not counted. The branch is the mirror: the project and branch are created when missing, and
  anything on the branch that isn't in the repository is deleted by the first revision, so give it its own project.
- `push` (`Push/PushRun.cs`), Vertex -> git or Perforce: reads the branch's revisions oldest first
  (`RevisionInterface.GetRevisionsAsync`/`GetRevisionChangesAsync`), writes each one's changed files into the
  target's working folder and makes one commit or changelist per revision, then checks again every `--interval`
  (default 1m), or exits once caught up with `--once`. Every commit carries a `Vertex-Revision: N` trailer; a run
  continues after the newest one it finds, so reruns and recoveries need no state file. A failure is logged and the
  next cycle re-syncs the target from its own history and carries on from the last revision that landed.
  - `--git <url or path>` (`Push/GitTarget.cs`): pushes to `--git-branch` (default: the Vertex branch's name). Commits
    are authored by the revision's submitter (`display name <username@server-host>`) at its time. The branch must be
    new, empty or made by vx-mirror. git must be able to log in without prompting (credential helper or SSH key).
  - `--p4 <client>` (`Push/PerforceTarget.cs`): submits through an existing client workspace (P4PORT/P4USER from p4's
    settings, or `--p4-port`/`--p4-user`); its Root and View decide where files land, and it should be used only by
    the mirror (pending changelists in it are deleted on recovery). Files are opened with `p4 edit`/`delete`/`add -f`
    and submitted as the Perforce user; the original submitter and time go in the description. Set the client's
    `LineEnd` to `share` so text files keep their exact bytes.
- Both turn off every line-ending conversion and filter in git clones (`Git.DisableConversions`), so bytes are exactly
  what's in the repository or Vertex.
- `VertexConnection.cs` (login, project and branch), `ExternalTool.cs` (runs git/p4 without a shell, never prompts),
  `MirrorLog.cs` (log file, `Format`, `PhaseStats` for the end-of-run table), `WorkFolder.cs`.

Build: `dotnet build Vertex.Mirror/Vertex.Mirror.csproj -c Release` (Release treats warnings as errors).
