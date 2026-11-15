# Vertex.Tests

`vx-bench`, a console app that load-tests a running Vertex server through the same client interfaces `vx` uses
(`FilesInterface`, `RevisionInterface`, `LockInterface`, `EventsInterface`), so its numbers are what users would see.
It has no unit tests (yet). See the root AGENTS.md for project-wide rules.

```sh
vx login http://127.0.0.1:5000
dotnet run -c Release --project Vertex.Tests -- all --server http://127.0.0.1:5000 --profile smoke
dotnet run -c Release --project Vertex.Tests -- locks --clients 64 --duration 2m
dotnet run -c Release --project Vertex.Tests -- soak                       # until Ctrl+C
```

- `Program.cs`: one command per scenario, plus `all` (every scenario except soak, in `ScenarioCatalog.All` order).
- `BenchSettings.cs` / `BenchProfile.cs`: flags and the smoke/standard/stress profiles; flags override single values.
- `BenchContext.cs`: resolves server and token (`--token`, `VX_BENCH_TOKEN`, then the `vx login` credential), creates
  a `bench-<time>` project (or uses `--project`), owns the scratch folder (system temp unless `--data`), and shares
  data sets between scenarios: the upload scenario's assets are what dedup and download reuse. All repository paths go
  under `bench/<run>/`, so runs can share a project.
- `DataGenerator.cs`: fast seeded content, unique per run, so nothing deduplicates against earlier runs by accident.
- `Metrics/`: `LatencyRecorder` (percentiles, errors classified as `http 429`, `grpc Unavailable`...), `ScenarioResult`
  and `Report` (console tables, `bench-results/bench-<run>.md` and `.json`).
- `Scenarios/`: one file per scenario; `Workers/` holds the simulated clients (submitter, locker, event subscriber)
  shared by the timed scenarios and soak. Each simulated client gets its own interface instances, so its own
  connection, like separate machines.

Rules for scenarios:

- Fail a result (`Check`/`Fail`) only for wrong answers (missing events, refused valid submits, corrupt downloads).
  Slow results go in the report and do not fail the run.
- Record every call through a `LatencyRecorder` so errors are counted; load loops use `TryTimeAsync` and keep going.
- Honour the cancellation token everywhere: Ctrl+C must still produce a report.
- Use only the client interfaces in `Vertex.Core`. If a benchmark needs a new server capability, add it to the server
  and its interface first.

Build: `dotnet build Vertex.Tests/Vertex.Tests.csproj -c Release` (Release treats warnings as errors).
