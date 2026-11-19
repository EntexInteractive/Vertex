# Vertex.Server

ASP.NET Core server (assembly `VertexServer`) hosting REST controllers and gRPC services. See the root AGENTS.md for
project-wide rules.

- `ServerApp.cs` wires everything: settings, Serilog, Kestrel (h2c, or HTTP/1.1 + HTTP/2 when TLS is set), DI,
  gRPC-Web, static files for the web UI, and controllers. Start here.
- `ServerSettings.cs` is the only place that reads configuration (`VX_*` environment variables). It validates every
  setting at startup and reports all errors at once. Add new settings here and document them in `docs/server/configuration.md`.
- Feature folders (`Projects/`, `Branches/`, `Files/`, `History/`, `Events/`, `Server/`) each pair a controller or
  gRPC service with a Dapper repository.
- `Telemetry/VertexMetrics.cs`: Vertex's own OpenTelemetry metrics. With `VX_METRICS_PORT` set, `ServerApp` exports
  them plus the built-in ASP.NET Core, Kestrel, runtime and Npgsql meters for Prometheus on `/metrics`, on that port
  only. Add a counter or histogram there for anything worth graphing, and list it in `docs/server/monitoring.md`; never
  write per-request rows to PostgreSQL.
- `History/`: revisions (`RevisionService`, `RevisionsRepository`) and file locks (`LockService`, `LocksRepository`).
  Every transaction that changes revisions or locks first locks the project's row in `projects.counters` (the
  `revision_counter` update, or `EventStore.LockProjectAsync`), so they serialize per project and submit's lock check
  can't race a new lock. That row is held until the commit, so a project does at most one write transaction per
  commit latency: keep the work done while holding it to as few round trips as possible (batch statements with
  `NpgsqlBatch`, never call S3 or other services inside). Lock and unlock requests go through `ProjectWriteQueue`,
  which runs the requests that arrive together for a project in one transaction (one statement each, one commit);
  `vertex.project_lock.held` and `vertex.db.commit` show where the time goes.
  `GetLockEvents` (for one-off commands) reads lock events from the event journal; keep its no-change path free of
  PostgreSQL (the latest seq is cached in Redis under `events:seq:{project}`, branch existence is cached too).
- Branches: creating a branch with a parent copies the parent's `revisions.head` rows (metadata only) in one
  transaction under the project's counters lock and records the parent's head as `branches.base_revision`; without a
  parent the branch starts empty. A branch's head revision is `GREATEST(base_revision, its own latest)`.
  `GetHeadChanges` syncs to the latest from `revisions.head`, or (an older `revision`, or a workspace synced before
  the base) rebuilds the full file list from history along the branch's parent chain
  (`RevisionsRepository.GetFilesAtRevisionAsync`) and sets `full`. Only that slow path reads history.
- Merges (`History/MergesRepository`, `Revisions.Merge`): between any two branches with a common ancestor (parent and
  child either way, or siblings), on file ids only (content is never read or merged). The base is the source side of
  the latest merge between the two in either direction (`revisions.merges`), or else their nearest common ancestor
  branch at the older of the two copies (`AncestorSql`). Paths whose head ids differ between the
  branches are compared with their id at the base: only the source changed it → take it; only the target → skip;
  both → conflict, which refuses the whole merge unless `resolve` picks a side. The plan is read without the counters
  lock; the write takes it, re-reads both heads and the latest merge in the same round trip, and replans if either
  moved. Locks on the target block the paths a merge would change. Every merge that settles anything is recorded,
  even with no revision, so a settled conflict never comes back.
- Merge chains (`History/AutoMerger`): a branch's `merge_to` (`vx branch --merge-to`) names the branch each of its
  revisions is merged into. `RevisionService.Submit` and `Merge` run the chain right after their revision commits
  (hop by hop, no resolution, stopping at the first hop that conflicts, is locked or has nothing to do) and return
  each hop in `auto_merges`; the revision that started it stands either way, and the next one catches up a missed
  hop. `BranchesRepository.SetMergeToAsync` rejects missing or unrelated targets and loops; a branch others merge
  into can't be deleted. Every merge's bookkeeping (notify, head cache, metrics) goes through `AutoMerger.PublishAsync`.
- `Events/`: the project event bus. `EventStore` is the journal (`events.events`, gapless seqs per project from
  `projects.counters.event_seq`, one event per operation with a JSON payload). Writers append in their transaction and call
  `INotificationService.NotifyAsync` after commit (see `ProjectCalls.AfterCommitAsync`). `NotificationService` holds
  one Redis pattern subscription (`events:*`) per instance and one `ProjectFeed` per project with live subscribers,
  which reads each new event once and fans it out to every local stream; without Redis it wakes its feeds directly.
  The journal is the truth and Redis only a wake-up, so a lost message delays an event but never loses it.
  `EventFeedService` streams events (`Events.proto`) with 30 s heartbeats and a 30 min maximum per stream.
  `EventDispatcher` runs `IEventHandler`s with durable per-project cursors (`events.cursors`, `FOR UPDATE SKIP
  LOCKED`), at least once and one instance at a time; `LoggingEventHandler` is registered in Development only.
  `EventPruner` deletes events older than 7 days.
- `Files/FileTransferService.cs` implements `Protos/Files.proto`: existence checks, batched chunk upload, manifests and
  chunk download. Uploads must stay unary because clients use gRPC-Web. `Storage/TransferBudget.cs` limits
  concurrent transfers. `Files/FilesRepository.cs` is the content index (`files.chunks`, `files.manifests`): which
  chunks and manifests are stored, answered for a whole request with one query and recorded with one commit per call.
  Never check existence with one S3 call per id.
- `Storage/S3ContentStore.cs` stores content-addressed chunk bytes in S3 (and reads manifests written before the
  content index existed). Keep the server stateless: any instance must be able to serve any request.
- `Database/` holds PostgreSQL access (`PostgresDatabase`, `DapperContext`), the Redis-backed cache (`CacheContext`,
  `RedisSubscriber`) and the schema migrations. Redis is optional; without it the server uses a memory-only cache.
- Caching: simple reads go through `DapperContext` with a key from `CacheKeys` (memory, then Redis, then PostgreSQL),
  and every write invalidates the keys it makes stale: `ExecuteAsync(..., invalidate)`, or `InvalidateAsync` after a
  transaction commits. Null results are cached too, so creating a row must invalidate its lookup keys. Values that
  many writes affect (lists, access levels) go under a generation key (`GenerationAsync`/`BumpAsync`). Cache a read
  when it is read far more than written and every write can invalidate it. Never cache reads that make a security
  decision (credentials, password hashes, the active-admin count) or reads inside a transaction that needs a
  snapshot. Monotonic counters (event seq, head revision) live in Redis with `SetMaxAsync`, so a slow reader can't
  write an older value back. Keys are case-sensitive: lowercase only names the database compares ignoring case.
  Cached values are serialized with Newtonsoft.Json (`NewtonsoftCacheSerializerFactory`), like the REST API.
- Schema migrations (`Database/Migrations/NNNN_name.sql`) are embedded in the assembly and applied by
  `DatabaseMigrator` (DbUp) before the server starts listening, each in its own transaction, under a PostgreSQL
  advisory lock so concurrent instances don't race. Applied names are recorded in `public.schema_migrations`. Until
  the first release, schema changes are appended idempotently to `0001_initial.sql` (see the root AGENTS.md); after
  it, add the next-numbered file and never edit a released one. The server refuses to start against a
  database that has migrations it doesn't know (it's older than the database). Timestamps are `BIGINT` Unix
  milliseconds.
- Multi-step writes, such as creating a project together with its default branch, run in one transaction.
- Authentication (`Auth/`): opaque `vxp_` bearer tokens from `POST /api/v1/auth/login`, stored as SHA-256 in
  `auth.tokens`. `TokenAuthenticationHandler` resolves them through the cache, so authenticated calls don't hit
  PostgreSQL. A fallback policy requires a logged-in user on every endpoint, REST and gRPC; opt out only with
  `[AllowAnonymous]`. Read the caller with `User.GetUserId()` and `User.IsServerAdmin()` (`VertexClaims`).
  `LoginThrottle` limits failed logins per username and per client address, with counters in Redis so the limits
  hold across instances. The client address comes from `X-Forwarded-For` only for trusted proxies (loopback and
  `VX_TRUSTED_PROXIES`).
- Permissions (`Access/AccessService`): fixed levels (read, write, manage) granted per project to users or groups,
  and a server-admin flag (`AccessLevel.Admin`, never granted). No custom roles and no default access. Check
  project-level actions against `Manage`, not `Admin`. Check with
  `AccessService.CheckAsync(User, projectId, level)` and return its status and message when denied. Anything that
  changes grants, group membership or admin flags must invalidate the access cache (`InvalidateAllAsync`).
- Users are never deleted, only disabled (or anonymized in place for an erasure request), because history refers to
  them. Foreign keys to `auth.users` from history or ownership use `ON DELETE RESTRICT`.
- The `Development` environment enables Swagger and gRPC reflection.

Build: `dotnet build Vertex.Server/Vertex.Server.csproj` from the repository root (this also builds `Vertex.Web`).
Run it locally against `docker compose up -d postgres redis rustfs rustfs-init`, with the variables in
`Properties/launchSettings.json` (git-ignored), and exercise changes with `vx` commands. Use
`docker compose up -d --build` to test the containerized server.
