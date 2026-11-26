# Vertex.Cli

The `vx` command line client, built on Spectre.Console.Cli. See the root AGENTS.md for project-wide rules.

- `Program.cs` sets up logging (`-log` enables debug output) and runs a single command, or an interactive prompt
  when started without arguments. `Startup.cs` registers every command; a command class does nothing until it is
  registered there.
- Commands are grouped by area: `Workspaces/` (`init`, `clone`), `Files/` (`status`, `submit`, `sync`, `revert`;
  `WorkspaceSettings.cs` has their shared settings, the linked-workspace lookup and path filtering), `Branches/`
  (`branch`, `checkout`, `merge`; `MergeOutput` is shared with `submit --merge-to` and prints merge chains with `PrintAutoMerges`; `vx branch` scans for unsubmitted changes before creating a branch from the workspace's branch), `Projects/`, `Profiles/`, `Net/` (`info`), `Utils/`.
- Submit, sync, revert and checkout logic lives in `Vertex.Core.Client/Workspaces` (`WorkspaceSubmit`,
  `WorkspaceSync`, ...) so the desktop app runs the same code; the commands here parse options, call it with
  `ConsoleReporter.Instance` (spinners and progress bars) and print the outcome. `Files/SyncOutput.cs` prints sync
  conflicts and transfer totals.
- `WorkspaceSync` is the sync `vx sync` (`-r` for any revision), `vx checkout` and `vx clone` run: one listing from
  `GetHeadChanges`, downloads only files whose content id differs from the index, and when the listing is full
  (`HeadChanges.Full`: a first sync, an older revision, or a new branch) removes tracked files that aren't in it.
  `checkout` refuses while there are local changes, forgets the old branch's locks (`WorkspaceLocks.ForgetBranch`),
  switches `.vx/config` first and then syncs from revision 0, so an interrupted checkout is finished by `vx sync`.
  The interactive session restarts its `WorkspaceMonitor` when a command changed the branch.
- `Files/TransferDisplay.cs` formats the progress bars `ConsoleReporter` draws from an operation's phases (fed by the
  `TransferProgress` that `FilesInterface.UploadAsync`/`DownloadAsync` fill once per batch).
- Shared options live in settings classes: `NetworkSettings` (`--server`, default `http://127.0.0.1:5000`),
  `ProjectSettings`, `BranchSettings` and `FilesSettings`. Extend these instead of redeclaring options.
- Server calls go through the interfaces in `Vertex.Core/Net/Interfaces`, and workspace logic belongs in
  `Vertex.Core.Client`. Keep commands thin: parse, call, print.
- Create interfaces with `new XInterface(settings.ServerAddress, settings.Token)`. `ServerAddress` resolves
  `--server`, then the workspace's server, then `vx config server` (`UserConfig`), then the default; `Token` comes from `vx login` (`CredentialStore`).
  A 401 throws `NotLoggedInException`, which `Program`'s error handler prints as one line naming `vx login`; don't
  handle it per command. Print other server failures with `ServerErrors.Print`.
- `Auth/` holds `login`, `logout` and password prompts; `Users/`, `Groups/` and `Access/` hold permission commands.

UX rules (ease of use is the top priority):

- Default to the current folder and to the workspace's project and branch. The common case shouldn't need flags.
- Make commands safe to re-run. Ask for confirmation only before destructive server-side actions (for example
  `vx branch -d`).
- Report an error as one red line that names the fix, e.g. "Project 'X' already exists. Use 'vx clone X' to get
  its files here."
- Escape user-supplied text with `Markup.Escape` before printing it as markup.
- Keep existing command names, option names, defaults and exit codes (0 for success, non-zero for failure).

Build: `dotnet build Vertex.Cli/Vertex.Cli.csproj` from the repository root. The output is `vx` in
`Vertex.Cli/bin/<Configuration>/net10.0/`. Run the affected command against a local server to verify a change.
