// Copyright 2026 Entex Interactive

using Grpc.Core;
using Vertex.Core.Net;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;
using Vertex.Server;

namespace Vertex.Core.Client.Workspaces
{
    public enum ActivityKind
    {
        Locked,
        Unlocked,
        Submitted
    }

    /// <summary>
    /// Something someone else did in the workspace's project: locked or released files, or submitted a revision.
    /// </summary>
    /// <param name="SameUser">Done by the same user from another workspace (another machine or folder).</param>
    /// <param name="Paths">For lock changes, the files.</param>
    public sealed record ActivityItem(ActivityKind Kind, string Username, string Host, bool SameUser, IReadOnlyList<string> Paths,
        long Revision = 0, string? Message = null, int Files = 0);

    /// <summary>
    /// Keeps a workspace's locks up to date in the background and reports what others do: locks files as they are
    /// saved, releases them when they go back to their base content, and follows the project's event stream so files
    /// others lock turn read-only here as soon as they do. Hosts (the interactive <c>vx</c> session, the GUI) start one
    /// per open workspace and show its events.
    /// </summary>
    /// <remarks>
    /// File system events only record paths. After <see cref="Debounce"/> of quiet, only the touched paths are checked
    /// (<see cref="WorkspaceScanner.ScanPaths"/>), so an editor saving a burst of files costs one batch. When the
    /// watcher overflows, one full scan runs instead. Other people's changes arrive over one long-lived stream
    /// (<see cref="EventsInterface"/>) that resumes from the last seq after any disconnect; a server too old for it is
    /// polled every <see cref="FallbackPollInterval"/> instead. Hosts must <see cref="PauseAsync"/> it around their own
    /// scans and lock steps so the index and the lock calls have a single writer.
    /// </remarks>
    public sealed class WorkspaceMonitor : IAsyncDisposable
    {
        /// <summary>Quiet time after the last file event before the touched files are checked.</summary>
        public static TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>How often to poll a server that has no event stream.</summary>
        public static TimeSpan FallbackPollInterval { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>The longest wait before reconnecting to an unreachable server.</summary>
        public static TimeSpan MaximumRetryInterval { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Activity is held this long before it's reported, so a lock that's released right away (a file touched but
        /// not changed) isn't reported at all, and a burst of operations comes out as one report each.
        /// </summary>
        public static TimeSpan ActivityDelay { get; set; } = TimeSpan.FromSeconds(1);

        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

        private readonly VWorkspace _workspace;
        private readonly string _serverAddress;
        private readonly Func<string?> _getToken;
        private string? _token;
        private LockInterface _client;
        private EventsInterface _events;
        private readonly Guid _projectId;
        private readonly Guid _branchId;
        private readonly Guid _workspaceId;
        private readonly string _username;
        private readonly string _metadataPrefix;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private readonly object _sync = new();
        private readonly HashSet<string> _pending = new(WorkspaceScanner.PathComparer);
        private readonly HashSet<string> _retry = new(WorkspaceScanner.PathComparer);
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private readonly List<EventMessage> _activity = new();

        private FileSystemWatcher? _watcher;
        private Task? _loop;
        private Task? _stream;
        private bool _fullScan = true; // Start from a full scan, so the state is right before the first save.
        private long _lastEventTicks;
        private long _lastActivityTicks;
        private long _lastAnnouncedSeq;
        private bool _polling;
        private DateTime _nextPoll = DateTime.MaxValue;
        private Exception? _problem;

        /// <summary>
        /// A file just changed here is locked by someone else. Raised once per file until it stops being a conflict.
        /// </summary>
        public event Action<HeldLock>? LockConflict;

        /// <summary>
        /// Someone else locked or released files, or submitted a revision.
        /// </summary>
        public event Action<ActivityItem>? Activity;

        /// <summary>
        /// The server became unreachable (with the reason) or reachable again (null). Raised on changes only.
        /// </summary>
        public event Action<Exception?>? ProblemChanged;

        /// <param name="getToken">Reads the current login token. It's read again when the server says the token is no
        /// longer valid, so logging in again elsewhere doesn't leave the monitor stuck.</param>
        public WorkspaceMonitor(VWorkspace workspace, string serverAddress, Func<string?> getToken, Guid projectId, Guid branchId, Guid workspaceId, string username)
        {
            _workspace = workspace;
            _serverAddress = serverAddress;
            _getToken = getToken;
            _token = getToken();
            _client = new LockInterface(serverAddress, _token);
            _events = new EventsInterface(serverAddress, _token);
            _projectId = projectId;
            _branchId = branchId;
            _workspaceId = workspaceId;
            _username = username;
            _metadataPrefix = Path.Combine(workspace.Root, VWorkspace.MetadataDirectoryName) + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// Starts watching. The first run is a full scan, like <c>vx status</c>.
        /// </summary>
        public void Start()
        {
            if (_loop is not null) return;

            _watcher = new FileSystemWatcher(_workspace.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };

            _watcher.Created += (_, e) => Record(e.FullPath);
            _watcher.Changed += (_, e) => Record(e.FullPath);
            _watcher.Deleted += (_, e) => Record(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Record(e.OldFullPath);
                Record(e.FullPath);
            };

            // Too many events at once: the buffer overflowed and paths were lost, so check everything once.
            _watcher.Error += (_, e) =>
            {
                Log.Warning(e.GetException(), "File watcher overflowed for {Root}; running a full scan", _workspace.Root);
                lock (_sync)
                {
                    _fullScan = true;
                    _lastEventTicks = DateTime.UtcNow.Ticks;
                }
            };

            _watcher.EnableRaisingEvents = true;
            _loop = Task.Run(() => LoopAsync(_stop.Token));
            _stream = Task.Run(() => StreamAsync(_stop.Token));
        }

        /// <summary>
        /// Waits for the current check to finish and holds further checks until <see cref="Resume"/>. File events
        /// and pushed events keep arriving meanwhile and are applied afterwards. Call it once per <see cref="Resume"/>.
        /// </summary>
        public Task PauseAsync(CancellationToken ct = default)
        {
            return _gate.WaitAsync(ct);
        }

        public void Resume()
        {
            _gate.Release();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _watcher?.Dispose();
            foreach (Task? task in new[] { _loop, _stream })
            {
                if (task is null) continue;
                try
                {
                    await task;
                }
                catch (Exception ex) when (ex is OperationCanceledException or RpcException)
                {
                    // Stopping.
                }
            }

            _client.Dispose();
            _events.Dispose();
            _stop.Dispose();
        }

        private void Record(string fullPath)
        {
            // Skip the .vx folder (and events for the folder itself) and sync's temporary downloads.
            if ((fullPath + Path.DirectorySeparatorChar).StartsWith(_metadataPrefix, StringComparison.OrdinalIgnoreCase)) return;
            if (fullPath.EndsWith(".vxtmp", StringComparison.OrdinalIgnoreCase)) return;
            if (!fullPath.StartsWith(_workspace.Root, StringComparison.OrdinalIgnoreCase) || fullPath.Length <= _workspace.Root.Length + 1) return;

            string relative = _workspace.ToRelativePath(fullPath);
            lock (_sync)
            {
                _pending.Add(relative);
                _lastEventTicks = DateTime.UtcNow.Ticks;
            }
        }

        /// <summary>
        /// Checks touched files, reports held activity, and polls when the server has no event stream.
        /// </summary>
        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Tick, ct);
                FlushActivity();

                (string[] paths, bool fullScan) = TakeBatch();
                bool poll = _polling && DateTime.UtcNow >= _nextPoll;
                if (paths.Length == 0 && !fullScan && !poll) continue;

                await _gate.WaitAsync(ct);
                try
                {
                    await RunOnceAsync(paths, fullScan, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // A local problem (e.g. the index is busy): try those paths again next time.
                    Log.Warning(ex, "Workspace monitor check failed for {Root}", _workspace.Root);
                    Requeue(paths);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        /// <summary>
        /// Follows the project's event stream: applies lock changes as they happen and collects others' activity.
        /// Reconnects with back-off and resumes from the last seq, so nothing is missed while disconnected.
        /// </summary>
        private async Task StreamAsync(CancellationToken ct)
        {
            TimeSpan retry = TimeSpan.FromSeconds(1);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    long seq = await WithIndexAsync(index => long.TryParse(index.GetMeta(WorkspaceIndex.EventSeqKey), out long stored) ? stored : 0, ct);
                    if (seq == 0)
                    {
                        // Nothing mirrored yet: load the current lock list, then follow from there.
                        await RefreshLocksAsync(ct);
                        seq = await WithIndexAsync(index => long.TryParse(index.GetMeta(WorkspaceIndex.EventSeqKey), out long stored) ? stored : 0, ct);
                    }

                    _lastAnnouncedSeq = Math.Max(_lastAnnouncedSeq, seq);
                    await foreach (EventMessage message in _events.SubscribeAsync(_projectId, _branchId, seq, ct))
                    {
                        retry = TimeSpan.FromSeconds(1);
                        SetProblem(null);

                        if (message.Resync is not null)
                        {
                            await RefreshLocksAsync(ct); // Too far behind for the stored events: reload the list.
                            continue;
                        }

                        Collect(message);
                        await WithIndexAsync(index =>
                        {
                            Locks(index).ApplyStreamed([message]);
                            return 0;
                        }, ct);
                    }

                    // The server ends streams now and then to spread clients over its instances; reconnect right away.
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
                {
                    // An older server: poll for lock changes instead.
                    Log.Information("The server has no event stream; polling for lock changes every {Interval}", FallbackPollInterval);
                    _polling = true;
                    _nextPoll = DateTime.UtcNow;
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is RpcException or HttpRequestException or NotLoggedInException or IOException)
                {
                    if (ct.IsCancellationRequested) return; // Shutting down cancels the stream; that's not a problem.
                    if (RefreshToken(ex)) continue;
                    SetProblem(ex);
                    await Task.Delay(retry, ct);
                    retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaximumRetryInterval.Ticks));
                }
            }
        }

        private async Task RefreshLocksAsync(CancellationToken ct)
        {
            LockReport report = await WithIndexAsync(index => Locks(index).RefreshAsync(ct), ct);
            if (report.Error is not null) throw report.Error;
        }

        /// <summary>
        /// Runs <paramref name="action"/> with the index while holding the gate, so it never overlaps a command or a
        /// file check.
        /// </summary>
        private async Task<T> WithIndexAsync<T>(Func<WorkspaceIndex, T> action, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                using WorkspaceIndex index = _workspace.OpenIndex();
                return action(index);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<T> WithIndexAsync<T>(Func<WorkspaceIndex, Task<T>> action, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                using WorkspaceIndex index = _workspace.OpenIndex();
                return await action(index);
            }
            finally
            {
                _gate.Release();
            }
        }

        private WorkspaceLocks Locks(WorkspaceIndex index)
        {
            return new WorkspaceLocks(_workspace, index, _client, _projectId, _branchId, _workspaceId, _username);
        }

        /// <summary>
        /// Holds an event by someone else (another user, or this user in another workspace) for <see cref="FlushActivity"/>.
        /// </summary>
        private void Collect(EventMessage message)
        {
            if (message.Seq <= _lastAnnouncedSeq || (message.Lock is null && message.Revision is null)) return;
            _lastAnnouncedSeq = message.Seq;
            if (Guid.TryParse(message.WorkspaceId, out Guid workspaceId) && workspaceId == _workspaceId) return;

            lock (_sync)
            {
                _activity.Add(message);
                _lastActivityTicks = DateTime.UtcNow.Ticks;
            }
        }

        /// <summary>
        /// Reports held activity once it has been quiet for <see cref="ActivityDelay"/>. A lock and its release by the same
        /// person in that window cancel out, and releases that came with a submit are left to the submit's report.
        /// </summary>
        private void FlushActivity()
        {
            List<EventMessage> held;
            lock (_sync)
            {
                if (_activity.Count == 0 || DateTime.UtcNow.Ticks - _lastActivityTicks < ActivityDelay.Ticks) return;
                held = _activity.ToList();
                _activity.Clear();
            }

            // Paths locked and released again by the same workspace within the window were only touched.
            Dictionary<(string Workspace, string Key), int> lockedAt = new();
            HashSet<(string Workspace, string Key)> cancelled = new();
            for (int i = 0; i < held.Count; i++)
            {
                if (held[i].Lock is not LockChange change) continue;
                foreach (string path in change.Paths)
                {
                    (string, string) id = (held[i].WorkspaceId, RepositoryPath.ToKey(path));
                    if (!change.Released) lockedAt[id] = i;
                    else if (change.Reason != "submitted" && lockedAt.Remove(id)) cancelled.Add(id);
                }
            }

            foreach (EventMessage message in held)
            {
                bool sameUser = message.Username == _username;
                if (message.Revision is RevisionCreated revision)
                {
                    Activity?.Invoke(new ActivityItem(ActivityKind.Submitted, message.Username, message.Host, sameUser, [], revision.Revision, revision.Message, revision.Files));
                    continue;
                }

                LockChange change = message.Lock;
                if (change.Released && change.Reason == "submitted") continue; // The submit itself is reported.

                List<string> paths = change.Paths.Where(p => !cancelled.Contains((message.WorkspaceId, RepositoryPath.ToKey(p)))).ToList();
                if (paths.Count == 0) continue;
                Activity?.Invoke(new ActivityItem(change.Released ? ActivityKind.Unlocked : ActivityKind.Locked, message.Username, message.Host, sameUser, paths));
            }
        }

        /// <summary>
        /// The touched paths, once events have been quiet for <see cref="Debounce"/>.
        /// </summary>
        private (string[] Paths, bool FullScan) TakeBatch()
        {
            lock (_sync)
            {
                if (_pending.Count == 0 && !_fullScan) return ([], false);
                if (DateTime.UtcNow.Ticks - _lastEventTicks < Debounce.Ticks) return ([], false);

                string[] paths = _pending.ToArray();
                bool fullScan = _fullScan;
                _pending.Clear();
                _fullScan = false;
                return (fullScan ? [] : paths, fullScan);
            }
        }

        private async Task RunOnceAsync(string[] paths, bool fullScan, CancellationToken ct)
        {
            using WorkspaceIndex index = _workspace.OpenIndex();
            WorkspaceLocks locks = Locks(index);

            LockReport report;
            IReadOnlyCollection<string> unchanged = [];
            List<string> changedPaths = new();
            if (fullScan)
            {
                WorkspaceStatus status = WorkspaceScanner.Scan(_workspace, index, cancellationToken: ct);
                report = await locks.ReconcileAsync(status.Changes, ct: ct);
                lock (_sync) _reported.IntersectWith(report.HeldByOthers.Select(h => RepositoryPath.ToKey(h.Path)));
            }
            else if (paths.Length > 0)
            {
                // Lock first on stats alone, so the lock doesn't wait for hashing (seconds for a multi-GB asset or a
                // burst of thousands of files). Then hash what moved and release the files that didn't really change.
                PathScan quick = WorkspaceScanner.ScanPaths(_workspace, index, paths, hash: false, ct);
                report = await locks.UpdateAsync(quick.Changed, quick.Unchanged, ct);
                if (report.Error is null && quick.Changed.Count > 0)
                {
                    // Conflicts are reported from the hashed pass only, so touching a file someone else holds
                    // without changing it doesn't raise a false alarm.
                    PathScan scan = WorkspaceScanner.ScanPaths(_workspace, index, quick.Changed.Select(c => c.Path).ToList(), hash: true, ct);
                    Requeue(scan.Unreadable); // Probably still being written; look again once it settles.
                    unchanged = scan.Unchanged;
                    report = await locks.UpdateAsync(scan.Changed, scan.Unchanged, ct);
                    changedPaths.AddRange(scan.Changed.Select(c => c.Path));
                }
                else
                {
                    unchanged = quick.Unchanged;
                    changedPaths.AddRange(quick.Changed.Select(c => c.Path));
                }
            }
            else
            {
                report = await locks.RefreshAsync(ct);
            }

            if (_polling) _nextPoll = DateTime.UtcNow + FallbackPollInterval;
            Report(report, changedPaths, unchanged, fullScan);
        }

        private void Report(LockReport report, List<string> changedPaths, IReadOnlyCollection<string> unchanged, bool fullScan)
        {
            if (report.Error is not null)
            {
                // A new login elsewhere revokes the old token: switch to the new one and try again right away.
                if (RefreshToken(report.Error))
                {
                    lock (_sync)
                    {
                        _pending.UnionWith(changedPaths);
                        if (fullScan) _fullScan = true;
                    }

                    return;
                }

                // Offline or logged out: keep the changed paths to lock once the server is back.
                lock (_sync) _retry.UnionWith(changedPaths);
                SetProblem(report.Error);
                return;
            }

            SetProblem(null);
            List<HeldLock> fresh = new();
            lock (_sync)
            {
                foreach (string path in unchanged) _reported.Remove(RepositoryPath.ToKey(path));
                foreach (HeldLock held in report.HeldByOthers)
                {
                    if (_reported.Add(RepositoryPath.ToKey(held.Path))) fresh.Add(held);
                }
            }

            foreach (HeldLock held in fresh) LockConflict?.Invoke(held);
        }

        /// <summary>
        /// Records whether the server is reachable, raising <see cref="ProblemChanged"/> only when that changes. Coming
        /// back online queues the files that changed meanwhile for locking.
        /// </summary>
        private void SetProblem(Exception? problem)
        {
            bool changed;
            lock (_sync)
            {
                changed = (_problem is null) != (problem is null);
                _problem = problem;
                if (problem is null && changed && _retry.Count > 0)
                {
                    _pending.UnionWith(_retry);
                    _retry.Clear();
                }
            }

            if (changed) ProblemChanged?.Invoke(problem);
        }

        /// <summary>
        /// When the server rejected the token and a newer one was saved (someone logged in again), switches to it.
        /// </summary>
        /// <returns>True when it switched, so the caller can retry at once.</returns>
        private bool RefreshToken(Exception ex)
        {
            if (ex is not (NotLoggedInException or RpcException { StatusCode: StatusCode.Unauthenticated })) return false;
            if (_getToken() is not string token || token == _token) return false;

            lock (_sync)
            {
                _token = token;
                _client.Dispose();
                _events.Dispose();
                _client = new LockInterface(_serverAddress, token);
                _events = new EventsInterface(_serverAddress, token);
            }

            return true;
        }

        private void Requeue(IEnumerable<string> paths)
        {
            lock (_sync)
            {
                foreach (string path in paths)
                {
                    _pending.Add(path);
                    _lastEventTicks = DateTime.UtcNow.Ticks;
                }
            }
        }
    }
}
