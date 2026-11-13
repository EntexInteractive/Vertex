## What does this change?

<!-- What the change does and why. Link the issue it addresses, e.g. "Fixes #123". -->

## How was it tested?

<!-- The commands you ran and against what (a local docker compose stack, sizes of test data, timings). -->

## Checklist

- [ ] Targets the `develop` branch
- [ ] `dotnet build Vertex.sln` succeeds (and `npm run lint` and `npm run build` in `Vertex.Web` for web changes)
- [ ] New server settings are read in `ServerSettings.cs` and documented in the README
- [ ] No secrets, credentials or local config files are included
