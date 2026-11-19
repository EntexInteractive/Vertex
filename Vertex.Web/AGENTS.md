# Vertex.Web

React + TypeScript + Tailwind CSS web UI, built with Vite. See the root AGENTS.md for project-wide rules.

- `README.md` describes the folder layout: the shell in `src/app/`, shared components in `src/components/`, one folder
  per page in `src/features/`, each with its own stylesheet imported from `src/styles/index.css` in cascade order.
- Colors come from the variables in `src/styles/tokens.css`; don't add new hex values. Server terms follow the root
  AGENTS.md: revisions (`#1234`), submit and sync, never commits, push or fetch.
- Pages for features that don't exist yet use `src/mocks/sampleData.ts` and must say so with `preview` on `ViewFrame`
  (or a `preview-tag`). Never mix made-up data into a screen without that tag.
- One build serves two hosts: `Vertex.Server` serves it from `wwwroot` and `Vertex.Gui` embeds it. Keep
  `base: './'` in `vite.config.ts` so asset URLs stay relative, and don't assume a browser-only or desktop-only
  environment.
- `src/host/` is the typed client for the desktop app's local API (`Vertex.Gui/Controllers`). `host-api.json` is
  written by building `Vertex.Gui` and `host-api.d.ts` is generated from it by `npm run gen:host` (part of
  `npm run build`); never edit either by hand. Call the API with `call(host.GET(...))` or `useHostQuery`, and check
  `hasDesktopHost()` before showing desktop-only features.
- Building either .NET host runs `npm ci` and `npm run build` here, so the build must pass from a clean install.
- Keep `package-lock.json` in sync with dependency changes.

From this directory: `npm ci` installs dependencies, `npm run dev` starts hot reload, `npm run lint` lints and
`npm run build` type-checks and bundles. For the desktop app with hot reload, run `npm run dev` and start Vertex.Gui
with `-debug`; Vite proxies `/api` to it.
