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

Asset URLs are relative (`base: './'` in `vite.config.ts`) and routes are hash routes (`#/history`), so the build works
under both hosts without server-side fallbacks. Calls to the desktop host go through `src/host/`; see the `Vertex.Gui`
README.

## Layout

```
src/
  main.tsx               entry point; imports host/client first so the desktop token is read before routing
  app/                   the shell: App, routes, AppLayout, session state (login, project, branch), workspace state
                         (WorkspaceProvider: the open workspace, its changes and the running operation, kept current
                         from host events) and shell actions (command palette, dialogs)
    layout/              context bar (workspace and branch pickers, main action), rail, status bar, command palette,
                         activity panel, result toast, and layout.css
  components/            shared UI: Icon, Avatar, SplitPane, VirtualList, ui.tsx (PaneHeader, Banner, Modal,
                         ContextMenu, ProgressBar, Kbd), EmptyState, and the legacy ViewFrame/DataTable for previews
  features/<name>/       one folder per page (welcome, changes, history, branches, locks, auth, and the tasks, bugs
                         and graph previews) with its stylesheet
  host/                  typed client for the desktop app's local API (host-api.* are generated, don't edit)
  lib/                   small helpers such as date and revision formatting
  mocks/sampleData.ts    made-up data for features that aren't built yet; pages using it show a "Preview" tag
  styles/                tokens.css (the color palette), base.css, and index.css, which imports every stylesheet
```

Every page uses the same frame: a list on the left and the selected item's details on the right (`SplitPane`).
Long lists use `VirtualList`, which only renders the rows in view, so 100,000 changes scroll smoothly. New areas
get a rail entry in `app/layout/Rail.tsx` and their actions in `app/layout/CommandPalette.tsx`; actions that don't
need a page only go in the palette.

Styles are plain CSS next to the components that use them, all imported in cascade order from `styles/index.css`.
Use the variables in `styles/tokens.css` (colors, type scale, spacing, radii) rather than new hex values or sizes. Fonts (Inter, JetBrains Mono) are bundled
with `@fontsource`, so the desktop app works offline.
