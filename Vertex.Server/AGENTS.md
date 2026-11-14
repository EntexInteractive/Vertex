# Vertex.Server

ASP.NET Core server (assembly `VertexServer`) hosting REST controllers and gRPC services. See the root AGENTS.md for
project-wide rules.

- `ServerApp.cs` wires everything: settings, Serilog, Kestrel (h2c, or HTTP/1.1 + HTTP/2 when TLS is set), DI,
  gRPC-Web, static files for the web UI, and controllers. Start here.
- `ServerSettings.cs` is the only place that reads configuration (`VX_*` environment variables). It validates every
  setting at startup and reports all errors at once. Add new settings here and document them in the README table.
- Feature folders (`Projects/`, `Branches/`, `Files/`, `Server/`, `Diagnostics/`) each pair a controller or gRPC
  service with a Dapper repository.
- `Files/FileTransferService.cs` implements `Protos/Files.proto`: existence checks, batched chunk upload, manifests and
  chunk download. Uploads must stay unary because clients use gRPC-Web. `Storage/TransferBudget.cs` limits
  concurrent transfers.
- `Storage/S3ContentStore.cs` stores content-addressed chunks and file manifests in S3. Keep the server stateless:
  any instance must be able to serve any request.
- `Database/` holds PostgreSQL access (`PostgresDatabase`, `DapperContext`), the Redis-backed cache (`CacheContext`,
  `RedisSubscriber`) and the schema migrations. Redis is optional; without it the server uses a memory-only cache.
- Schema migrations (`Database/Migrations/NNNN_name.sql`) are embedded in the assembly and applied by
  `DatabaseMigrator` (DbUp) before the server starts listening, each in its own transaction, under a PostgreSQL
  advisory lock so concurrent instances don't race. Applied names are recorded in `public.schema_migrations`. To
  change the schema, add the next-numbered file; never edit a merged one. The server refuses to start against a
  database that has migrations it doesn't know (it's older than the database). Timestamps are `BIGINT` Unix
  milliseconds.
- Multi-step writes, such as creating a project together with its default branch, run in one transaction.
- Authentication (`Auth/`): opaque `vxp_` bearer tokens from `POST /api/v1/auth/login`, stored as SHA-256 in
  `auth.tokens`. `TokenAuthenticationHandler` resolves them through the cache, so authenticated calls don't hit
  PostgreSQL. A fallback policy requires a logged-in user on every endpoint, REST and gRPC; opt out only with
  `[AllowAnonymous]`. Read the caller with `User.GetUserId()` and `User.IsServerAdmin()` (`VertexClaims`).
- Permissions (`Access/AccessService`): fixed levels (read, write, admin) granted per project to users or groups,
  a server-admin flag, and project visibility. No custom roles. Check with
  `AccessService.CheckAsync(User, projectId, level)` and return its status and message when denied. Anything that
  changes grants, group membership or admin flags must invalidate the access cache (`InvalidateAllAsync`).
- The `Development` environment enables Swagger and gRPC reflection.

Build: `dotnet build Vertex.Server/Vertex.Server.csproj` from the repository root (this also builds `Vertex.Web`).
Run it locally against `docker compose up -d postgres redis rustfs rustfs-init`, with the variables in
`Properties/launchSettings.json` (git-ignored), and exercise changes with `vx` commands. Use
`docker compose up -d --build` to test the containerized server.
