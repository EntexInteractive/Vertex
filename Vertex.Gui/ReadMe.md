# Vertex.Gui

The Vertex desktop app. It's a small [Photino.NET](https://www.tryphotino.io/) window that shows the shared web UI from
[`Vertex.Web`](../Vertex.Web), plus a local ASP.NET Core API that the UI calls for anything only the desktop can do
(workspaces, logins, file system).

## How it works

- Building this project runs `npm ci` and `npm run build` in `Vertex.Web` (see `Vertex.React.targets` at the repository
  root) and embeds the output as resources, so the published app is a single executable.
- At startup `Program.cs` starts Kestrel on `127.0.0.1` with an OS-chosen port. It serves the embedded UI and the API
  under `/api` (ordinary controllers in `Controllers/`), then opens the window on `http://127.0.0.1:<port>/#token=<token>`.
- The token is 256 random bits, new for every launch. The page reads it from the URL fragment (which never leaves the
  window), keeps it in `sessionStorage` and sends it as `X-Vertex-Token`. `Hosting/HostSecurityMiddleware.cs` rejects
  any `/api` call with the wrong token, a `Host` other than our loopback address (DNS rebinding) or a foreign `Origin`.
  No CORS policy is registered, so web pages in the user's browser can't call the API.
- Push notifications (lock conflicts, progress, ...) go through `HostEvents.Publish(type, data)` and reach the page over
  `GET /api/events` (Server-Sent Events, with a heartbeat every 15 s).

## Adding an API call

1. Add an action to a controller in `Controllers/` (or a new `[ApiController]`). Use records for request and response
   types, take a `CancellationToken`, and throw `HostProblemException` for failures, with a message that names the
   command that fixes it.
2. Build `Vertex.Gui`. The build writes the OpenAPI document to `Vertex.Web/src/host/host-api.json`; then run
   `npm run gen:host` in `Vertex.Web` (or build again) to regenerate the TypeScript types in `host-api.d.ts`.
   Commit both files with the C# change. If the web build fails because it uses the new call before the document
   has it, run `dotnet build Vertex.Gui -p:SkipVertexReact=true` first to regenerate the document alone.
3. Call it from React; paths, parameters and results are all typed:

   ```tsx
   import {call, host} from "../host/client.ts";
   import {useHostQuery} from "../host/useHost.ts";

   const session = useHostQuery(signal => call(host.GET("/api/session", {signal})), []);
   ```

`call` throws a `HostError` whose `message` is the server's `detail`. The same UI also runs in the browser from
`Vertex.Server`; check `hasDesktopHost()` before showing desktop-only features (`useHostQuery` stays idle there).

To react to host events, use `useHostEvents(event => ...)`, which switches on `event.type`.

## Building and running

From the repository root:

```sh
dotnet build Vertex.Gui/Vertex.Gui.csproj
dotnet run --project Vertex.Gui
```

For hot reload, run `npm run dev` in `Vertex.Web` and start the app with `-debug`
(`dotnet run --project Vertex.Gui -- -debug`). The window then loads the Vite dev server, which proxies `/api` to the
host on port 5180 (`VX_GUI_PORT` overrides it on both sides). Without a running Vite server, `-debug` falls back to the
embedded UI. `-debug` also enables the WebView DevTools and serves the live OpenAPI document at `/api/openapi.json`.

To drive the app from scripts or a headless browser, also set `VX_GUI_TOKEN` (32+ letters and digits) in a `-debug`
run. The API then accepts that token, and `http://127.0.0.1:5180/#/history?token=<token>` opens a page directly.
Normal launches always use a random token.

## Talking to the Vertex server

Controllers reach the server through `Hosting/ServerSession.cs`, which reads the server and login from the same files
`vx config` and `vx login` use, so the app and the CLI always agree. `Hosting/ServerErrorHandler.cs` turns server
errors into one-line messages that name the fix (the CLI's wording); throw `HostProblemException` for your own.
