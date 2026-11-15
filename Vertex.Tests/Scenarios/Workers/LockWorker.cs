// Copyright 2026 Entex Interactive

using Vertex.Core.Net.Interfaces;
using Vertex.Server;
using Vertex.Tests.Metrics;

namespace Vertex.Tests.Scenarios.Workers
{
    /// <summary>
    /// One simulated workspace locking and releasing files, the way the workspace monitor does when files are saved
    /// and reverted. A "hot" worker fights over a small shared set of files with the other hot workers.
    /// </summary>
    public sealed class LockWorker : IDisposable
    {
        public const int HotPaths = 20;

        private readonly BenchContext _context;
        private readonly int _index;
        private readonly bool _hot;
        private readonly Random _random;
        private readonly LockInterface _locks;
        private readonly LatencyRecorder _lock;
        private readonly LatencyRecorder _unlock;
        private long _granted;
        private long _held;
        private int _cycle;

        public LockWorker(BenchContext context, int index, bool hot, LatencyRecorder lockTimer, LatencyRecorder unlockTimer)
        {
            _context = context;
            _index = index;
            _hot = hot;
            _lock = lockTimer;
            _unlock = unlockTimer;
            _random = new Random(index * 7919);
            _locks = context.Locks();
        }

        public Guid WorkspaceId { get; } = Guid.NewGuid();

        public long Granted => Interlocked.Read(ref _granted);

        public long Held => Interlocked.Read(ref _held);

        /// <remarks>
        /// With the profile's think time, each lock and unlock cycle is followed by a pause, like saves in an editor.
        /// </remarks>
        public async Task RunAsync(Func<bool> until, CancellationToken ct)
        {
            ThinkTime think = _context.Profile.LockThink;
            await Task.Delay(think.Stagger(_random), ct);
            while (!until() && !ct.IsCancellationRequested)
            {
                string[] paths = NextPaths();
                (bool ok, List<LockResult>? results) = await _lock.TryTimeAsync(() =>
                    _locks.LockAsync(_context.ProjectId, _context.BranchId, WorkspaceId, $"bench-{_index:D3}", paths, ct), ct);
                if (!ok)
                {
                    await Task.Delay(250, ct);
                    continue;
                }

                string[] mine = results!.Where(r => r.Status is LockStatus.Granted or LockStatus.AlreadyYours).Select(r => r.Path).ToArray();
                Interlocked.Add(ref _granted, results!.Count(r => r.Status == LockStatus.Granted));
                Interlocked.Add(ref _held, results!.Count(r => r.Status == LockStatus.Held));
                if (mine.Length > 0)
                    await _unlock.TryTimeAsync(() => _locks.UnlockAsync(_context.ProjectId, _context.BranchId, WorkspaceId, mine, ct: ct), ct);

                if (!think.IsNone) await Task.Delay(think.Next(_random), ct);
            }
        }

        private string[] NextPaths()
        {
            if (_hot)
                return Enumerable.Range(0, HotPaths).OrderBy(_ => _random.Next()).Take(5).Select(i => $"{_context.PathPrefix}/locks/hot/File{i:D2}.uasset").ToArray();

            int start = _cycle++ * 10 % 1000;
            return Enumerable.Range(start, 10).Select(i => $"{_context.PathPrefix}/locks/w{_index:D3}/File{i:D4}.uasset").ToArray();
        }

        public void Dispose()
        {
            _locks.Dispose();
        }
    }
}
