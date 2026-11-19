# Vertex.Mirror

`vx-mirror`, a console app that mirrors a Vertex branch to or from other version control systems, through the same
client interfaces `vx` uses. See the root AGENTS.md for project-wide rules.

```sh
vx login http://127.0.0.1:5000
# git or Perforce -> Vertex, from the first commit/changelist, one every 5s, checking every byte; reruns resume
dotnet run -c Release --project Vertex.Mirror -- pull --project gitignore --git https://github.com/github/gitignore.git
dotnet run -c Release --project Vertex.Mirror -- pull --project game --p4 //depot/game/... --p4-port ssl:p4.studio.com:1666
# Vertex -> git or Perforce, every new revision as it lands
dotnet run -c Release --project Vertex.Mirror -- push --project game --git git@github.com:studio/game-mirror.git
dotnet run -c Release --project Vertex.Mirror -- push --project game --p4 game-mirror-ws --once
```

Both commands log in like `vx` (`--server`, `--token`, `VX_MIRROR_TOKEN`, then the `vx login` credential), take the
Vertex side as `--project` and `--branch` (default `main`), run until `--duration` or Ctrl+C, keep their working
copies in `--work` (default: a temp folder, deleted at the end), and write `<command>-<run>.log` under `--out`
(default `./mirror-results`). Exit code 0 means no problems, 1 problems (see the log), 2 a setup error.

- `pull` (`Pull/PullRun.cs`), git or Perforce -> Vertex, through an `IPullSource`: `--git <url or path>`
  (`Pull/GitSource.cs`, needs git; walks the default branch's first-parent line) or `--p4 <depot path>`
  (`Pull/PerforceSource.cs`, needs p4; makes its own client `vx-mirror-<machine>-<run>` with Root `<work>/p4`,
  `LineEnd: share` and `nomodtime`, syncs `@N` per changelist, and deletes the client at the end). It starts at the
  first commit or changelist (or `--start`: a commit number/sha/tag for git, a changelist number for Perforce) and every
  `--interval` (default 5s) submits the next one like `vx submit`, files at the source's own paths. Each revision's
  message is the source message plus `Authored by <author> at <time> UTC.` and a `Git-Commit: <sha>` or
  `Perforce-Change: <n>` trailer (`Trailer.cs`); a rerun without `--start` finds the newest trailer in the branch's last
  100 revisions and resumes there. Once caught up it checks the source for new changes once a minute, and idle
  cycles do nothing. After each submit it syncs the revision into a second folder like `vx sync` and checks the file
  set, sizes and SHA-256 of every downloaded file against the source's copy; on the first cycle, when it catches up
  and every `--full-check-every` cycles (default 100) it also compares the whole listing and re-downloads everything.
  One CSV row per cycle (`pull-<run>.csv`), mismatched files kept as `.vertex`/`.source` pairs in
  `pull-<run>-evidence/`. Problems are counted and the run keeps going; failures to reach the source (offline) are
  logged, not counted. The branch is the mirror: the project and branch are created when missing, and anything on the
  branch that isn't in the source is deleted by the first revision, so give it its own project.
- `push` (`Push/PushRun.cs`), Vertex -> git or Perforce: reads the branch's revisions oldest first
  (`RevisionInterface.GetRevisionsAsync`/`GetRevisionChangesAsync`), writes each one's changed files into the
  target's working folder and makes one commit or changelist per revision, then checks again every `--interval`
  (default 1m), or exits once caught up with `--once`. Every commit carries a `Vertex-Revision: N` trailer (`Trailer.cs`); a run
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
  `P4.cs` (p4 for one client: harmless-warning handling, `-x` file lists, `-ztag` parsing),
  `MirrorLog.cs` (log file, `Format`, `PhaseStats` for the end-of-run table), `WorkFolder.cs`.

Build: `dotnet build Vertex.Mirror/Vertex.Mirror.csproj -c Release` (Release treats warnings as errors).
