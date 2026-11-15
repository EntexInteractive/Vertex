// Copyright 2026 Entex Interactive

using Grpc.Core;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;
using Vertex.Server;
using Vertex.Tests.Metrics;

namespace Vertex.Tests.Scenarios.Workers
{
    /// <summary>
    /// One simulated developer: its own workspace and connections, editing 1-20 of its 50 files and submitting them
    /// as a revision, over and over. Paths never overlap other workers', so every refusal is a real problem.
    /// </summary>
    public sealed class SubmitWorker : IDisposable
    {
        private const int Slots = 50;

        private readonly BenchContext _context;
        private readonly int _index;
        private readonly string _folder;
        private readonly Random _random;
        private readonly FilesInterface _files;
        private readonly RevisionInterface _revisions;
        private readonly LatencyRecorder _upload;
        private readonly LatencyRecorder _submit;
        private readonly Dictionary<int, string> _current = new();
        private readonly Dictionary<int, string> _previous = new();
        private long _revisionsSubmitted;
        private long _filesSubmitted;

        public SubmitWorker(BenchContext context, int index, LatencyRecorder upload, LatencyRecorder submit)
        {
            _context = context;
            _index = index;
            _upload = upload;
            _submit = submit;
            _random = new Random(index);
            _folder = context.NewDirectory($"worker-{index:D3}");
            _files = context.Files(2);
            _revisions = context.Revisions();
        }

        public Guid WorkspaceId { get; } = Guid.NewGuid();

        public long RevisionsSubmitted => Interlocked.Read(ref _revisionsSubmitted);

        public long FilesSubmitted => Interlocked.Read(ref _filesSubmitted);

        /// <summary>
        /// Submits until <paramref name="until"/> passes or <paramref name="ct"/> is cancelled.
        /// </summary>
        public async Task RunAsync(Func<bool> until, CancellationToken ct)
        {
            while (!until() && !ct.IsCancellationRequested)
            {
                int[] slots = Enumerable.Range(0, Slots).OrderBy(_ => _random.Next()).Take(_random.Next(1, 21)).ToArray();
                if (!await SubmitAsync(slots, ct)) await Task.Delay(250, ct);
            }
        }

        /// <summary>
        /// Writes new content to the slots, uploads it and submits it against the last known bases.
        /// </summary>
        public async Task<bool> SubmitAsync(int[] slots, CancellationToken ct)
        {
            (ChunkedFile[] chunked, FileChange[] changes) = Prepare(slots, null);
            (bool uploaded, UploadResult? _) = await _upload.TryTimeAsync(() => _files.UploadAsync(chunked, ct: ct), ct);
            if (!uploaded) return false;

            (bool submitted, long _) = await _submit.TryTimeAsync(() =>
                _revisions.SubmitAsync(_context.ProjectId, _context.BranchId, WorkspaceId, Host, $"Bench worker {_index}", changes, ct), ct);
            if (!submitted) return false;

            for (int i = 0; i < slots.Length; i++)
            {
                if (_current.TryGetValue(slots[i], out string? old)) _previous[slots[i]] = old;
                _current[slots[i]] = chunked[i].FileId;
            }

            Interlocked.Increment(ref _revisionsSubmitted);
            Interlocked.Add(ref _filesSubmitted, slots.Length);
            return true;
        }

        /// <summary>
        /// Submits a change based on an outdated version of a file, which the server must refuse.
        /// </summary>
        /// <returns>True when the server refused it with FailedPrecondition.</returns>
        public async Task<bool> SubmitStaleAsync(CancellationToken ct)
        {
            // Make sure slot 0 has an older version to be stale against.
            while (!_previous.ContainsKey(0))
            {
                if (!await SubmitAsync([0], ct)) return false;
            }

            (ChunkedFile[] chunked, FileChange[] changes) = Prepare([0], _previous[0]);
            await _files.UploadAsync(chunked, ct: ct);
            try
            {
                await _revisions.SubmitAsync(_context.ProjectId, _context.BranchId, WorkspaceId, Host, "Bench stale submit", changes, ct);
                return false;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
            {
                return true;
            }
        }

        private string Host => $"bench-{_index:D3}";

        private (ChunkedFile[] Chunked, FileChange[] Changes) Prepare(int[] slots, string? baseOverride)
        {
            ChunkedFile[] chunked = new ChunkedFile[slots.Length];
            FileChange[] changes = new FileChange[slots.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                string local = Path.Combine(_folder, $"File{slots[i]:D2}.dat");
                _context.Data.WriteFile(local, _random.Next(4 * 1024, 32 * 1024), _context.Data.NextStream(), true);
                chunked[i] = ContentChunker.ChunkFile(local);
                changes[i] = new FileChange
                {
                    Path = $"{_context.PathPrefix}/workers/w{_index:D3}/File{slots[i]:D2}.dat",
                    FileId = chunked[i].FileId,
                    BaseFileId = baseOverride ?? _current.GetValueOrDefault(slots[i], string.Empty)
                };
            }

            return (chunked, changes);
        }

        public void Dispose()
        {
            _files.Dispose();
            _revisions.Dispose();
        }
    }
}
