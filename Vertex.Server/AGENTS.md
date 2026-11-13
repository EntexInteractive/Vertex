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
  `RedisSubscriber`) and `schema.sql`. The schema must stay idempotent and in dependency order, and timestamps are
  `BIGINT` Unix milliseconds. Redis is optional; without it the server uses a memory-only cache.
- Multi-step writes, such as creating a project together with its default branch, run in one transaction.
- The `Development` environment enables Swagger and gRPC reflection.

Build: `dotnet build Vertex.Server/Vertex.Server.csproj` from the repository root (this also builds `Vertex.Web`).
Run it locally against `docker compose up -d postgres redis rustfs rustfs-init`, with the variables in
`Properties/launchSettings.json` (git-ignored), and exercise changes with `vx` commands. Use
`docker compose up -d --build` to test the containerized server.
