# Vertex.Gui

Photino desktop app (assembly `Vertex`) that hosts the shared web UI from `../Vertex.Web`. See the root AGENTS.md for
project-wide rules.

- `Program.cs` starts a Photino static file server for the embedded web build, opens the window and handles UI
  messages in `HandleUiMessage`.
- `../Vertex.React.targets` embeds the web build at build time. This project has no UI source folder of its own.
- The UI talks to C# through `Vertex.Web/src/backend.ts`. To add a call, add it to the allowlist `switch` in
  `HandleUiMessage` and to `BackendMethods` in `backend.ts`, and change both in the same commit. Never handle a
  method the allowlist doesn't name.
- `ReadMe.md` documents the bridge for people.

Build: `dotnet build Vertex.Gui/Vertex.Gui.csproj` from the repository root, then check the interaction in the running
app (`dotnet run --project Vertex.Gui`).
