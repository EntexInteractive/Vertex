# Vertex.Gui

The Vertex desktop app. It's a small [Photino.NET](https://www.tryphotino.io/) host that opens a native window and
shows the shared web UI from [`Vertex.Web`](../Vertex.Web).

## How it works

- Building this project runs `npm ci` and `npm run build` in `Vertex.Web` (see `Vertex.React.targets` at the repository
  root) and embeds the output as resources. `Photino.NET.Server` serves the embedded files from a local static file
  server, so the published app is a single executable with no separate web files.
- The UI calls C# through Photino's web message bridge. `Vertex.Web/src/backend.ts` sends
  `{ type: "request", id, method, payload }`, and `Program.HandleUiMessage` answers with a matching response.

## Adding a backend method

1. Add the method to the allowlist `switch` in `Program.HandleUiMessage`. Only methods listed there can be called.
2. Add its payload and result types to `BackendMethods` in `Vertex.Web/src/backend.ts`.
3. Call it from React:

   ```tsx
   const greeting = await callBackend("app.getGreeting", { name: "Ada" });
   ```

`callBackend` throws when the page runs in a normal browser, so check `hasPhotinoBackend()` before showing desktop-only
features.

## Building

From the repository root:

```sh
dotnet build Vertex.Gui/Vertex.Gui.csproj
dotnet run --project Vertex.Gui
```
