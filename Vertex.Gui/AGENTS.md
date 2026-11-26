# Vertex.Gui

Photino desktop app (assembly `Vertex`) that hosts the shared web UI from `../Vertex.Web` and a local ASP.NET Core API
for it. See the root AGENTS.md for project-wide rules and `ReadMe.md` for how the bridge works.

- `Program.cs` builds the `WebApplication` (Kestrel on loopback, embedded static files, controllers), starts it, then
  opens the window on it. Keep everything before `builder.Build()` side-effect free: the build-time OpenAPI generator
  runs `Main` and stops at `Build()`.
- The UI talks to C# only through HTTP controllers in `Controllers/`, called from `Vertex.Web/src/host/client.ts`.
  To add a call, add a controller action, build this project (it writes `Vertex.Web/src/host/host-api.json`), run
  `npm run gen:host` in `Vertex.Web`, and commit the C# change, `host-api.json` and `host-api.d.ts` together.
- `Hosting/HostSecurityMiddleware.cs` guards every `/api` route (token, `Host`, `Origin`). Never bind to anything but
  loopback, never add a CORS policy, never return or log the token or a saved login token.
- Push state changes to the UI with `HostEvents.Publish`; don't make the UI poll.
- Call the Vertex server through `ServerSession.Connect` and let exceptions reach `ServerErrorHandler`, or throw
  `HostProblemException` with a message that names the fix. Don't catch and swallow server errors in controllers.
- `Hosting/WorkspaceRegistry.cs` holds the open workspace, the recent list (`workspaces.json` next to the CLI's
  credentials) and the one `WorkspaceMonitor` that locks files as they're saved. Anything that touches the workspace
  index runs inside `ExclusiveAsync`, which pauses the monitor. Monitor results reach the UI as `workspace.changed`
  (rescan), `workspace.activity`, `workspace.lockConflict` and `workspace.problem` events.
- `Hosting/OperationRunner.cs` runs one long operation (submit, sync, revert, checkout, clone) at a time in the
  background and pushes `operation.progress` / `operation.done`. The operations themselves are the Core.Client ones
  (`WorkspaceSubmit`, `WorkspaceSync`, ...) the CLI runs too; don't copy their logic here.
- `Hosting/DesktopShell.cs` is the native folder picker and "show in file manager".
- `../Vertex.React.targets` embeds the web build at build time. This project has no UI source folder of its own.

Build: `dotnet build Vertex.Gui/Vertex.Gui.csproj` from the repository root, then check the interaction in the running
app (`dotnet run --project Vertex.Gui`, or `-- -debug` with `npm run dev` for hot reload).
