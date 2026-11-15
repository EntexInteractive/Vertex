// Copyright 2026 Entex Interactive

using System.Diagnostics;
using System.Net;
using Vertex.Core.Client.Auth;
using Vertex.Core.Client.Config;
using Vertex.Core.Data;
using Vertex.Core.Net;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;
using Vertex.Core.Storage;
using Vertex.Tests.Metrics;

namespace Vertex.Tests
{
    /// <summary>
    /// A setup problem the user can fix; the message names how.
    /// </summary>
    public sealed class BenchSetupException(string message) : Exception(message);

    /// <summary>
    /// One benchmark run: the server, login and project it runs against, its scratch folder, and data sets shared
    /// between scenarios (the upload scenario's assets are what the download scenario fetches).
    /// </summary>
    public sealed class BenchContext : IDisposable
    {
        public const string DefaultServer = "http://127.0.0.1:5000";

        private readonly bool _keepData;
        private readonly SemaphoreSlim _dataGate = new(1, 1);
        private IReadOnlyList<ChunkedFile>? _assets;
        private bool _assetsUploaded;
        private IReadOnlyList<(ChunkedFile Original, ChunkedFile Edited)>? _edited;
        private bool _editedUploaded;
        private ChunkedFile? _soakAsset;

        private BenchContext(BenchSettings settings, BenchProfile profile, string server, string token, string runId, string dataRoot)
        {
            Profile = profile;
            Server = server;
            Token = token;
            RunId = runId;
            DataRoot = dataRoot;
            _keepData = settings.KeepData;
            SoakDuration = settings.ParsedDuration;
            PathPrefix = $"bench/{runId}";
            Data = new DataGenerator((ulong)DateTime.UtcNow.Ticks ^ (ulong)Environment.ProcessId << 40);
        }

        public BenchProfile Profile { get; }

        /// <summary>
        /// --duration when given; without it a soak runs until Ctrl+C.
        /// </summary>
        public TimeSpan? SoakDuration { get; }

        public string Server { get; }

        public string Token { get; }

        public string RunId { get; }

        public string DataRoot { get; }

        /// <summary>
        /// Repository folder every path this run writes goes under, so runs sharing a project never collide.
        /// </summary>
        public string PathPrefix { get; }

        public DataGenerator Data { get; }

        public string Username { get; private set; } = string.Empty;

        public InfoResponse? ServerInfo { get; private set; }

        public string ProjectName { get; private set; } = string.Empty;

        public Guid ProjectId { get; private set; }

        public Guid BranchId { get; private set; }

        public bool CreatedProject { get; private set; }

        /// <summary>
        /// Resolves the server and login, checks the server answers, and creates (or finds) the project.
        /// </summary>
        public static async Task<BenchContext> CreateAsync(BenchSettings settings, CancellationToken ct)
        {
            BenchProfile profile = BenchProfile.Resolve(settings);
            string server = (settings.Server ?? UserConfig.Load().Server ?? DefaultServer).TrimEnd('/');
            string? token = settings.Token ?? Environment.GetEnvironmentVariable("VX_BENCH_TOKEN") ?? CredentialStore.Get(server)?.Token;
            if (string.IsNullOrEmpty(token))
            {
                // A typo in the address should say so, not suggest logging in to a server that isn't there.
                using ServerInterface probe = new(server);
                ServerResult<InfoResponse> reachable = await probe.GetInfoAsync(ct);
                if (!reachable.IsSuccessStatusCode) throw new BenchSetupException($"Can't reach {server}: {reachable.Exception?.Message}. Check the address, or pass --server.");
                throw new BenchSetupException($"Not logged in to {server}. Run 'vx login {server}' first, or pass --token.");
            }

            string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string dataRoot = Path.Combine(settings.Data ?? Path.Combine(Path.GetTempPath(), "vx-bench"), runId);
            Directory.CreateDirectory(dataRoot);

            BenchContext context = new(settings, profile, server, token, runId, dataRoot);
            try
            {
                await context.ConnectAsync(settings.Project, ct);
                return context;
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }

        private async Task ConnectAsync(string? projectName, CancellationToken ct)
        {
            using ServerInterface serverApi = new(Server, Token);
            ServerResult<InfoResponse> info = await serverApi.GetInfoAsync(ct);
            if (!info.IsSuccessStatusCode) throw new BenchSetupException($"Can't reach {Server}: {info.Exception?.Message}. Check the address, or pass --server.");
            ServerInfo = info.Data;

            using AuthInterface auth = new(Server, Token);
            try
            {
                Username = Unwrap(await auth.GetCurrentUserAsync(ct)).Username;
            }
            catch (NotLoggedInException)
            {
                throw new BenchSetupException($"The login for {Server} was revoked or has expired. Run 'vx login {Server}' again.");
            }

            using ProjectInterface projects = new(Server, Token);
            ProjectName = projectName ?? $"bench-{RunId}";
            ServerResult<VProject> existing = await projects.GetProjectByNameAsync(ProjectName, ct);
            if (existing.IsSuccessStatusCode && existing.Data is not null)
            {
                ProjectId = existing.Data.Id;
                using BranchInterface branches = new(Server, Token);
                IReadOnlyList<VBranch> list = Unwrap(await branches.ListBranchesAsync(ProjectId, ct));
                VBranch branch = list.FirstOrDefault(b => b.Name == "main") ?? list.FirstOrDefault() ?? throw new BenchSetupException($"Project {ProjectName} has no branches.");
                BranchId = branch.Id;
                return;
            }

            CreateProjectResponse created = Unwrap(await projects.CreateProjectAsync(ProjectName, "main", ct));
            ProjectId = created.Project.Id;
            BranchId = created.DefaultBranch.Id;
            CreatedProject = true;
        }

        public FilesInterface Files(int concurrency)
        {
            return new FilesInterface(Server, Token) { MaxConcurrency = concurrency };
        }

        public RevisionInterface Revisions()
        {
            return new RevisionInterface(Server, Token);
        }

        public LockInterface Locks()
        {
            return new LockInterface(Server, Token);
        }

        public EventsInterface Events()
        {
            return new EventsInterface(Server, Token);
        }

        /// <summary>
        /// A new empty folder in the run's scratch space.
        /// </summary>
        public string NewDirectory(string name)
        {
            string path = Path.Combine(DataRoot, name);
            if (Directory.Exists(path)) Directory.Delete(path, true);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// Refuses to start when the data folder's drive can't hold <paramref name="bytes"/> more.
        /// </summary>
        public void RequireFreeSpace(long bytes, string what)
        {
            DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(DataRoot))!);
            long margin = 1024L * 1024 * 1024;
            if (drive.AvailableFreeSpace < bytes + margin)
                throw new BenchSetupException($"{what} needs about {Format.Bytes(bytes + margin)} free on {drive.Name}, which has {Format.Bytes(drive.AvailableFreeSpace)}. " +
                                              "Pass --data <folder> on a bigger drive, or a smaller --profile/--large-size.");
        }

        /// <summary>
        /// The large assets, generated and chunked on first use. Generation and hashing times go into
        /// <paramref name="result"/>.
        /// </summary>
        public async Task<IReadOnlyList<ChunkedFile>> GetAssetsAsync(ScenarioResult result, CancellationToken ct)
        {
            await _dataGate.WaitAsync(ct);
            try
            {
                if (_assets is not null) return _assets;

                // Source assets, a few edited copies and one download folder at a time.
                RequireFreeSpace(Profile.LargeTotalBytes * 2 + Math.Min(4, Profile.LargeFiles) * Profile.LargeFileSize, "The transfer scenarios");
                string folder = NewDirectory("assets");
                Stopwatch timer = Stopwatch.StartNew();
                IReadOnlyList<string> paths = await Task.Run(() => Data.WriteAssets(folder, Profile.LargeFiles, Profile.LargeFileSize, ct), ct);
                result.Throughput("Generate assets (local disk)", Profile.LargeTotalBytes, timer.Elapsed, $"{paths.Count} files");

                timer.Restart();
                ChunkedFile[] chunked = await Task.Run(() => DataGenerator.Chunk(paths, ct), ct);
                result.Throughput("Chunk + hash assets (client CPU)", Profile.LargeTotalBytes, timer.Elapsed, $"{chunked.Sum(c => c.Chunks.Count):N0} chunks");
                return _assets = chunked;
            }
            finally
            {
                _dataGate.Release();
            }
        }

        /// <summary>
        /// Uploads the assets unless a scenario already did.
        /// </summary>
        public async Task<IReadOnlyList<ChunkedFile>> EnsureAssetsUploadedAsync(ScenarioResult result, CancellationToken ct)
        {
            IReadOnlyList<ChunkedFile> assets = await GetAssetsAsync(result, ct);
            if (_assetsUploaded) return assets;

            using FilesInterface files = Files(Profile.MaximumConcurrency);
            Stopwatch timer = Stopwatch.StartNew();
            UploadResult upload = await files.UploadAsync(assets.ToArray(), ct: ct);
            result.Throughput("Setup: upload assets", upload.RawBytesUploaded, timer.Elapsed);
            MarkAssetsUploaded();
            return assets;
        }

        public void MarkAssetsUploaded()
        {
            _assetsUploaded = true;
        }

        /// <summary>
        /// Copies of the first few assets with 5% of each rewritten, chunked, paired with their originals.
        /// </summary>
        public async Task<IReadOnlyList<(ChunkedFile Original, ChunkedFile Edited)>> GetEditedAssetsAsync(ScenarioResult result, CancellationToken ct)
        {
            IReadOnlyList<ChunkedFile> assets = await GetAssetsAsync(result, ct);
            await _dataGate.WaitAsync(ct);
            try
            {
                if (_edited is not null) return _edited;
                string folder = NewDirectory("edited");
                ChunkedFile[] originals = assets.Take(4).ToArray();
                string[] paths = originals.Select(o => Path.Combine(folder, Path.GetFileName(o.Path))).ToArray();
                await Task.Run(() => Parallel.For(0, originals.Length, i => Data.Mutate(originals[i].Path, paths[i], 0.05)), ct);
                ChunkedFile[] edited = await Task.Run(() => DataGenerator.Chunk(paths, ct), ct);
                return _edited = originals.Zip(edited).ToArray();
            }
            finally
            {
                _dataGate.Release();
            }
        }

        public async Task<IReadOnlyList<(ChunkedFile Original, ChunkedFile Edited)>> EnsureEditedUploadedAsync(ScenarioResult result, CancellationToken ct)
        {
            await EnsureAssetsUploadedAsync(result, ct);
            IReadOnlyList<(ChunkedFile Original, ChunkedFile Edited)> edited = await GetEditedAssetsAsync(result, ct);
            if (_editedUploaded) return edited;

            using FilesInterface files = Files(Profile.MaximumConcurrency);
            await files.UploadAsync(edited.Select(e => e.Edited).ToArray(), ct: ct);
            MarkEditedUploaded();
            return edited;
        }

        public void MarkEditedUploaded()
        {
            _editedUploaded = true;
        }

        /// <summary>
        /// One modest uploaded asset for the soak's periodic downloads, so a soak alone doesn't generate gigabytes.
        /// </summary>
        public async Task<ChunkedFile> GetSoakAssetAsync(CancellationToken ct)
        {
            if (_soakAsset is not null) return _soakAsset;
            if (_assetsUploaded && _assets is { Count: > 0 }) return _soakAsset = _assets[0];

            string path = Path.Combine(NewDirectory("soak-asset"), "Soak.uasset");
            await Task.Run(() => Data.WriteFile(path, Math.Min(Profile.LargeFileSize, 64L * 1024 * 1024), Data.NextStream(), false), ct);
            ChunkedFile chunked = await Task.Run(() => ContentChunker.ChunkFile(path, ct), ct);
            using FilesInterface files = Files(4);
            await files.UploadAsync([chunked], ct: ct);
            return _soakAsset = chunked;
        }

        /// <summary>
        /// The data of a successful REST result, or an <see cref="HttpRequestException"/> carrying its status.
        /// </summary>
        public static T Unwrap<T>(ServerResult<T> result)
        {
            if (result.IsSuccessStatusCode && result.Data is not null) return result.Data;
            throw result.Exception as HttpRequestException
                  ?? new HttpRequestException(result.Exception?.Message ?? "Request failed.", result.Exception, result.StatusCode == 0 ? null : (HttpStatusCode)result.StatusCode);
        }

        public void Dispose()
        {
            _dataGate.Dispose();
            if (_keepData) return;
            try
            {
                if (Directory.Exists(DataRoot)) Directory.Delete(DataRoot, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine($"[yellow]Could not delete {Markup.Escape(DataRoot)}: {Markup.Escape(ex.Message)}[/]");
            }
        }
    }
}
