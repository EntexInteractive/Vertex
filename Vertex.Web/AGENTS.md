# Vertex.Web

React + TypeScript + Tailwind CSS web UI, built with Vite. See the root AGENTS.md for project-wide rules.

- Entry points are `src/main.tsx` and `src/App.tsx`; components live in `src/components/`.
- One build serves two hosts: `Vertex.Server` serves it from `wwwroot` and `Vertex.Gui` embeds it. Keep
  `base: './'` in `vite.config.ts` so asset URLs stay relative, and don't assume a browser-only or desktop-only
  environment.
- `src/backend.ts` is the typed bridge to the desktop host. Check `hasPhotinoBackend()` before using desktop-only
  features, and keep `BackendMethods` in sync with the allowlist in `Vertex.Gui/Program.cs`.
- Building either .NET host runs `npm ci` and `npm run build` here, so the build must pass from a clean install.
- Keep `package-lock.json` in sync with dependency changes.

From this directory: `npm ci` installs dependencies, `npm run dev` starts hot reload, `npm run lint` lints and
`npm run build` type-checks and bundles.
