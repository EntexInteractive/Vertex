# Vertex.Web

The Vertex web UI, built with React, TypeScript, Tailwind CSS and [Vite](https://vite.dev/). The same build is served
by `Vertex.Server` from its `wwwroot` and embedded in the [`Vertex.Gui`](../Vertex.Gui) desktop app. Building either
of those .NET projects builds this app automatically (see `Vertex.React.targets` at the repository root).

## Development

Requires Node.js 22 LTS or later.

```sh
npm ci          # install dependencies from package-lock.json
npm run dev     # start the Vite dev server with hot reload
npm run lint    # run ESLint
npm run build   # type-check and build to dist/
```

Asset URLs are relative (`base: './'` in `vite.config.ts`) so the build works both under ASP.NET Core and Photino's
local file server. Calls to the desktop host go through `src/backend.ts`; see the `Vertex.Gui` README.
