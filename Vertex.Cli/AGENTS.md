# Vertex.Cli

The `vx` command line client, built on Spectre.Console.Cli. See the root AGENTS.md for project-wide rules.

- `Program.cs` sets up logging (`-log` enables debug output) and runs a single command, or an interactive prompt
  when started without arguments. `Startup.cs` registers every command; a command class does nothing until it is
  registered there.
- Commands are grouped by area: `Workspaces/` (`init`, `fetch`), `Files/` (`status`, `push`, `pull`; `submit` and
  `sync` are empty placeholders), `Branches/` (`branch`), `Projects/`, `Profiles/`, `Net/` (`info`), `Utils/`.
- Shared options live in settings classes: `NetworkSettings` (`--server`, default `http://127.0.0.1:5000`),
  `ProjectSettings`, `BranchSettings` and `FilesSettings`. Extend these instead of redeclaring options.
- Server calls go through the interfaces in `Vertex.Core/Net/Interfaces`, and workspace logic belongs in
  `Vertex.Core.Client`. Keep commands thin: parse, call, print.
- Create interfaces with `new XInterface(settings.ServerAddress, settings.Token)`. `ServerAddress` resolves
  `--server`, then the workspace's server, then the default; `Token` comes from `vx login` (`CredentialStore`).
  A 401 throws `NotLoggedInException`, which `Program`'s error handler prints as one line naming `vx login`; don't
  handle it per command. Print other server failures with `ServerErrors.Print`.
- `Auth/` holds `login`, `logout` and password prompts; `Users/`, `Groups/` and `Access/` hold permission commands.

UX rules (ease of use is the top priority):

- Default to the current folder and to the workspace's project and branch. The common case shouldn't need flags.
- Make commands safe to re-run. Ask for confirmation only before destructive server-side actions (for example
  `vx branch -d`).
- Report an error as one red line that names the fix, e.g. "Project 'X' already exists. Use 'vx fetch X' to link
  this folder to it."
- Escape user-supplied text with `Markup.Escape` before printing it as markup.
- Keep existing command names, option names, defaults and exit codes (0 for success, non-zero for failure).

Build: `dotnet build Vertex.Cli/Vertex.Cli.csproj` from the repository root. The output is `vx` in
`Vertex.Cli/bin/<Configuration>/net10.0/`. Run the affected command against a local server to verify a change.
