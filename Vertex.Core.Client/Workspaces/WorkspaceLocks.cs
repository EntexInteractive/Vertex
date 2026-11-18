// Copyright 2026 Entex Interactive

using Grpc.Core;
using Vertex.Core.Client.IO;
using Vertex.Core.Net;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;
using Vertex.Server;

namespace Vertex.Core.Client.Workspaces
{
    /// <summary>
    /// A file changed in this workspace that someone else has locked.
    /// </summary>
    /// <param name="SameUser">Locked by the same user in another workspace.</param>
    public sealed record HeldLock(string Path, string Username, string Host, bool SameUser);

    /// <summary>
    /// The workspace's lock state after <see cref="WorkspaceLocks.ReconcileAsync"/>.
    /// </summary>
    public sealed class LockReport
    {
        /// <summary>Changed files locked by someone else, ordered by path. They can't be submitted.</summary>
        public required IReadOnlyList<HeldLock> HeldByOthers { get; init; }

        /// <summary>Files this workspace holds locks on.</summary>
        public int LockedByMe { get; init; }

        /// <summary>Files someone else holds locks on (read-only here unless changed here too).</summary>
        public int LockedByOthers { get; init; }

        /// <summary>Why the server couldn't be asked (offline, not logged in), or null. Local state is unchanged then.</summary>
        public Exception? Error { get; init; }
    }

    /// <summary>
    /// Keeps a workspace's locks in step with its changes: locks every changed file, releases locks on files that
    /// are no longer changed, and mirrors everyone else's locks so files they hold are read-only here.
    /// </summary>
    /// <remarks>
    /// Costs at most three calls per run, and only one (a cheap "anything since seq N?") when nothing changed. Read-only
    /// flags are only flipped for files whose lock changed, and never on a file changed here, so an open editor can
    /// still save; that file is reported in <see cref="LockReport.HeldByOthers"/> instead.
    /// </remarks>
    public sealed class WorkspaceLocks
    {
        /// <summary>How long one run may wait on the server, so commands never hang on a dead connection.</summary>
        public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>This machine's name, shown to others next to locks taken here.</summary>
        public static string Host => Environment.MachineName;

        /// <summary>
        /// Drops the mirror of the current branch's locks before the workspace moves to another branch: files made
        /// read-only for someone's lock become writable again, the locks table is emptied and the event seq reset, so
        /// the next lock step loads the new branch's locks from scratch.
        /// </summary>
        public static void ForgetBranch(VWorkspace workspace, WorkspaceIndex index)
        {
            List<LockEntry> locks = index.ReadLocks();
            foreach (LockEntry entry in locks.Where(l => l.MadeReadOnly))
            {
                string fullPath = workspace.ToFullPath(entry.Path);
                if (File.Exists(fullPath)) FileSystemUtility.MakeWritable(fullPath);
            }

            index.ApplyLocks([], locks.Select(l => l.PathKey), 0);
        }

        private readonly VWorkspace _workspace;
        private readonly WorkspaceIndex _index;
        private readonly LockInterface _client;
        private readonly Guid _projectId;
        private readonly Guid _branchId;
        private readonly Guid _workspaceId;
        private readonly string _username;

        public WorkspaceLocks(VWorkspace workspace, WorkspaceIndex index, LockInterface client, Guid projectId, Guid branchId, Guid workspaceId, string username)
        {
            _workspace = workspace;
            _index = index;
            _client = client;
            _projectId = projectId;
            _branchId = branchId;
            _workspaceId = workspaceId;
            _username = username;
        }

        /// <summary>
        /// Locks the changed files, releases this workspace's other locks, and applies everyone's lock changes.
        /// </summary>
        /// <param name="changes">Every change in the workspace (not just a filtered part of it), as the last scan found.</param>
        /// <param name="rewritten">Files just written (e.g. by sync), whose read-only flag must be checked again.</param>
        public Task<LockReport> ReconcileAsync(IReadOnlyCollection<WorkspaceChange> changes, IEnumerable<string>? rewritten = null, CancellationToken ct = default)
        {
            return RunAsync(changes, null, rewritten, ct);
        }

        /// <summary>
        /// Like <see cref="ReconcileAsync"/> for a partial scan (<see cref="WorkspaceScanner.ScanPaths"/>): locks
        /// <paramref name="changed"/>, and releases this workspace's locks only among <paramref name="unchanged"/>,
        /// leaving every other lock alone.
        /// </summary>
        /// <remarks><see cref="LockReport.HeldByOthers"/> covers only <paramref name="changed"/>.</remarks>
        public Task<LockReport> UpdateAsync(IReadOnlyCollection<WorkspaceChange> changed, IReadOnlyCollection<string> unchanged, CancellationToken ct = default)
        {
            HashSet<string> releasable = unchanged.Select(RepositoryPath.ToKey).ToHashSet(StringComparer.Ordinal);
            return RunAsync(changed, releasable, null, ct);
        }

        /// <summary>
        /// Only applies everyone's lock changes, without locking or releasing anything.
        /// </summary>
        public Task<LockReport> RefreshAsync(CancellationToken ct = default)
        {
            return RunAsync(null, null, null, ct);
        }

        /// <summary>
        /// Applies events pushed by the event stream (<c>EventFeed.Subscribe</c>) to the lock mirror, without calling the
        /// server, and stores the stream's seq. Events at or before the stored seq were already applied and are skipped.
        /// </summary>
        public void ApplyStreamed(IReadOnlyList<EventMessage> messages)
        {
            if (messages.Count == 0) return;
            long.TryParse(_index.GetMeta(WorkspaceIndex.EventSeqKey), out long seq);
            List<EventMessage> fresh = messages.Where(m => m.Seq > seq).ToList();
            if (fresh.Count == 0) return;

            long latest = fresh.Max(m => m.Seq);
            if (fresh.All(m => m.Lock is null))
            {
                // Heartbeats and other kinds of events only move the cursor.
                _index.SetMeta(WorkspaceIndex.EventSeqKey, latest.ToString());
                return;
            }

            Dictionary<string, LockEntry> before = _index.ReadLocks().ToDictionary(l => l.PathKey, StringComparer.Ordinal);
            Dictionary<string, LockEntry> locks = new(before, StringComparer.Ordinal);
            Dictionary<string, string> touched = new(StringComparer.Ordinal);
            foreach (EventMessage message in fresh.Where(m => m.Lock is not null))
            {
                bool mine = Guid.TryParse(message.WorkspaceId, out Guid workspaceId) && workspaceId == _workspaceId;
                foreach (string path in message.Lock.Paths)
                {
                    string key = RepositoryPath.ToKey(path);
                    touched[key] = path;
                    if (message.Lock.Released)
                    {
                        locks.Remove(key);
                    }
                    else
                    {
                        bool madeReadOnly = locks.TryGetValue(key, out LockEntry? existing) && existing.MadeReadOnly;
                        locks[key] = new LockEntry(key, path, mine, message.Username, message.Host, message.CreatedAt, madeReadOnly);
                    }
                }
            }

            UpdateReadOnly(before, locks, touched, (_, path) => IsChangedHere(path));
            List<LockEntry> upserts = locks.Values.Where(l => !before.TryGetValue(l.PathKey, out LockEntry? old) || old != l).ToList();
            List<string> deletes = before.Keys.Where(k => !locks.ContainsKey(k)).ToList();
            _index.ApplyLocks(upserts, deletes, latest);
        }

        /// <summary>
        /// True when a file differs from what was last submitted or synced, by the index and a stat; it's never hashed.
        /// </summary>
        private bool IsChangedHere(string path)
        {
            FileInfo file = new(_workspace.ToFullPath(path));
            IndexEntry? entry = _index.Find([path], []).FirstOrDefault();
            if (entry is null) return file.Exists;
            if (!file.Exists || entry.BaseId != entry.FileId) return true;
            return file.Length != entry.Size || file.LastWriteTimeUtc.Ticks != entry.LastWriteTicks;
        }

        /// <summary>
        /// The locks as last fetched, ordered by path.
        /// </summary>
        public IReadOnlyList<LockEntry> Current()
        {
            return _index.ReadLocks().OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <param name="releasable">Keys this run may release; null releases every lock of ours not in <paramref name="changes"/>.</param>
        private async Task<LockReport> RunAsync(IReadOnlyCollection<WorkspaceChange>? changes, IReadOnlySet<string>? releasable, IEnumerable<string>? rewritten, CancellationToken ct)
        {
            Dictionary<string, LockEntry> before = _index.ReadLocks().ToDictionary(l => l.PathKey, StringComparer.Ordinal);
            Dictionary<string, LockEntry> locks = new(before, StringComparer.Ordinal);
            long.TryParse(_index.GetMeta(WorkspaceIndex.EventSeqKey), out long seq);

            // Paths to lock: every changed path, both ends of a move, deletions included.
            Dictionary<string, string>? desired = null;
            if (changes is not null)
            {
                desired = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (WorkspaceChange change in changes)
                {
                    desired.TryAdd(RepositoryPath.ToKey(change.Path), change.Path);
                    if (change.PreviousPath is not null) desired.TryAdd(RepositoryPath.ToKey(change.PreviousPath), change.PreviousPath);
                }
            }

            // Every path whose lock may have changed, with how to spell it, for the read-only pass.
            Dictionary<string, string> touched = new(StringComparer.Ordinal);
            foreach (string path in rewritten ?? []) touched[RepositoryPath.ToKey(path)] = path;

            Exception? error = null;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                if (desired is not null)
                {
                    await LockChangedAsync(desired, locks, touched, timeout.Token);
                    await ReleaseUnchangedAsync(desired, releasable, locks, touched, timeout.Token);
                }

                seq = await ApplyEventsAsync(seq, locks, touched, timeout.Token);
            }
            catch (Exception ex) when (IsServerProblem(ex) && !ct.IsCancellationRequested)
            {
                // Keep whatever was learned; the seq stays put, so the next run fetches the rest again.
                error = ex;
            }

            UpdateReadOnly(before, locks, touched, (key, _) => desired?.ContainsKey(key) == true);

            List<LockEntry> upserts = locks.Values.Where(l => !before.TryGetValue(l.PathKey, out LockEntry? old) || old != l).ToList();
            List<string> deletes = before.Keys.Where(k => !locks.ContainsKey(k)).ToList();
            _index.ApplyLocks(upserts, deletes, seq);

            List<HeldLock> held = new();
            if (desired is not null)
            {
                foreach ((string key, string path) in desired)
                {
                    if (locks.TryGetValue(key, out LockEntry? entry) && !entry.Mine)
                        held.Add(new HeldLock(path, entry.Username, entry.Host, entry.Username == _username));
                }
            }

            return new LockReport
            {
                HeldByOthers = held.OrderBy(h => h.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                LockedByMe = locks.Values.Count(l => l.Mine),
                LockedByOthers = locks.Values.Count(l => !l.Mine),
                Error = error
            };
        }

        private async Task LockChangedAsync(Dictionary<string, string> desired, Dictionary<string, LockEntry> locks, Dictionary<string, string> touched, CancellationToken ct)
        {
            List<string> toLock = desired.Where(d => !(locks.TryGetValue(d.Key, out LockEntry? entry) && entry.Mine)).Select(d => d.Value).ToList();
            if (toLock.Count == 0) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (LockResult result in await _client.LockAsync(_projectId, _branchId, _workspaceId, Host, toLock, ct))
            {
                string key = RepositoryPath.ToKey(result.Path);
                locks.TryGetValue(key, out LockEntry? existing);
                locks[key] = result.Status == LockStatus.Held
                    ? new LockEntry(key, result.Path, false, result.Username, result.Host, existing?.CreatedAt ?? now, existing?.MadeReadOnly ?? false)
                    : new LockEntry(key, result.Path, true, _username, Host, existing is { Mine: true } ? existing.CreatedAt : now, existing?.MadeReadOnly ?? false);

                touched[key] = result.Path;
            }
        }

        private async Task ReleaseUnchangedAsync(Dictionary<string, string> desired, IReadOnlySet<string>? releasable, Dictionary<string, LockEntry> locks,
            Dictionary<string, string> touched, CancellationToken ct)
        {
            List<LockEntry> stale = locks.Values.Where(l => l.Mine && !desired.ContainsKey(l.PathKey) && (releasable is null || releasable.Contains(l.PathKey))).ToList();
            if (stale.Count == 0) return;

            await _client.UnlockAsync(_projectId, _branchId, _workspaceId, stale.Select(l => l.Path).ToList(), ct: ct);
            foreach (LockEntry entry in stale)
            {
                locks.Remove(entry.PathKey);
                touched[entry.PathKey] = entry.Path;
            }
        }

        /// <summary>
        /// Applies the lock events since <paramref name="seq"/> (or the full list when the server resets) to the mirror.
        /// </summary>
        /// <returns>The seq the mirror is now at.</returns>
        private async Task<long> ApplyEventsAsync(long seq, Dictionary<string, LockEntry> locks, Dictionary<string, string> touched, CancellationToken ct)
        {
            GetLockEventsResponse response = await _client.GetLockEventsAsync(_projectId, _branchId, seq, ct);
            if (response.Reset)
            {
                Dictionary<string, LockEntry> fresh = new(StringComparer.Ordinal);
                foreach (LockInfo info in response.Locks) fresh[RepositoryPath.ToKey(info.Path)] = ToEntry(info, locks);

                foreach (LockEntry entry in locks.Values) touched[entry.PathKey] = entry.Path;
                locks.Clear();
                foreach ((string key, LockEntry entry) in fresh)
                {
                    locks[key] = entry;
                    touched[key] = entry.Path;
                }
            }
            else
            {
                foreach (LockEvent lockEvent in response.Events)
                {
                    string key = RepositoryPath.ToKey(lockEvent.Lock.Path);
                    touched[key] = lockEvent.Lock.Path;
                    if (lockEvent.Released) locks.Remove(key);
                    else locks[key] = ToEntry(lockEvent.Lock, locks);
                }
            }

            return response.LatestSeq;
        }

        private LockEntry ToEntry(LockInfo info, Dictionary<string, LockEntry> current)
        {
            string key = RepositoryPath.ToKey(info.Path);
            bool madeReadOnly = current.TryGetValue(key, out LockEntry? existing) && existing.MadeReadOnly;
            bool mine = Guid.TryParse(info.WorkspaceId, out Guid workspaceId) && workspaceId == _workspaceId;
            return new LockEntry(key, info.Path, mine, info.Username, info.Host, info.CreatedAt, madeReadOnly);
        }

        /// <summary>
        /// Makes files locked by others read-only and gives back write access when their lock goes away, but only for
        /// paths whose lock changed in this run.
        /// </summary>
        /// <param name="changedHere">True for a path changed in this workspace, which is left writable.</param>
        private void UpdateReadOnly(Dictionary<string, LockEntry> before, Dictionary<string, LockEntry> locks, Dictionary<string, string> touched,
            Func<string, string, bool> changedHere)
        {
            foreach ((string key, string path) in touched)
            {
                bool wasMadeReadOnly = before.TryGetValue(key, out LockEntry? old) && old.MadeReadOnly;
                string fullPath = _workspace.ToFullPath(locks.TryGetValue(key, out LockEntry? entry) ? entry.Path : path);
                try
                {
                    if (entry is { Mine: false })
                    {
                        // Changed here too: leave it writable so the user's editor can still save; it's reported instead.
                        if (changedHere(key, entry.Path)) continue;

                        bool made = FileSystemUtility.MakeReadOnly(fullPath);
                        if (made || wasMadeReadOnly) locks[key] = entry with { MadeReadOnly = true };
                    }
                    else if (wasMadeReadOnly)
                    {
                        FileSystemUtility.MakeWritable(fullPath);
                        if (entry is not null) locks[key] = entry with { MadeReadOnly = false };
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "Could not change the read-only flag of {Path}", path);
                }
            }
        }

        private static bool IsServerProblem(Exception ex)
        {
            return ex is RpcException or HttpRequestException or NotLoggedInException or OperationCanceledException;
        }
    }
}
