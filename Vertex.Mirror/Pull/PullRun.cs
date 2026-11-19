// Copyright 2026 Entex Interactive

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Storage;
using Vertex.Server;

namespace Vertex.Mirror.Pull
{
    /// <summary>
    /// git or Perforce -> Vertex: walks a source's history from its first commit or changelist (or --start, or where
    /// the last run stopped), submitting each one as a revision every --interval, syncs every revision back into a
    /// second folder the way <c>vx sync</c> does, and compares every byte with the source's copy. Once caught up it
    /// keeps following new changes until the duration passes or Ctrl+C.
    /// </summary>
    /// <remarks>
    /// The branch is the mirror: files go at the source's own paths, each change is one revision with the change's
    /// message plus its author, time and a trailer naming it (<c>Git-Commit: sha</c>, <c>Perforce-Change: N</c>), and
    /// anything on the branch that isn't in the source is deleted by the first revision. A rerun reads the newest
    /// trailer on the branch and continues from there. Every cycle is one line on the console and one row in
    /// <c>pull-&lt;run&gt;.csv</c>; every problem goes to <c>pull-&lt;run&gt;.log</c>, and files that came back different
    /// are kept under <c>pull-&lt;run&gt;-evidence/</c>.
    /// </remarks>
    public sealed class PullRun
    {
        private const int DownloadParallelism = 8;
        private const string Host = "vx-mirror";

        /// <summary>Newest revisions read at start to find the last trailer.</summary>
        private const int ResumeSearch = 100;

        /// <summary>Time between checks for new changes once caught up, so an idle mirror doesn't hammer the source.</summary>
        private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(1);

        private readonly VertexConnection _vertex;
        private readonly IPullSource _source;
        private readonly MirrorLog _log;
        private readonly string _work;
        private readonly string _sync;
        private readonly string _csvPath;
        private readonly string _evidence;
        private readonly Guid _workspaceId = Guid.NewGuid();
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

        private readonly PhaseStats _cycleTimer = new("Cycle (scan to verified)");
        private readonly PhaseStats _scanTimer = new("Scan + hash changed files");
        private readonly PhaseStats _uploadTimer = new("Upload changed content");
        private readonly PhaseStats _submitTimer = new("Submit revision");
        private readonly PhaseStats _syncTimer = new("Sync (listing + downloads)");
        private readonly PhaseStats _verifyTimer = new("Verify synced files");
        private readonly PhaseStats _fullTimer = new("Full check (listing + fresh download)");
        private readonly PhaseStats _fetchTimer = new("Check source for new changes");

        // The clone as last scanned (by relative path), and what the server holds for it (by path key).
        private Dictionary<string, LocalFile> _local = new(StringComparer.Ordinal);
        private Dictionary<string, LocalFile> _expected = new(StringComparer.Ordinal);
        private Dictionary<string, SubmittedFile> _submitted = new(StringComparer.Ordinal);

        private long _syncRevision;
        private long _lastRevision;
        private int _cycle;
        private int _cycleProblems;
        private bool _caughtUp;

        /// <summary>The run started at the change the branch already holds (from its trailer), so cycle 1 adds nothing new.</summary>
        private bool _resumed;

        private long _changesMirrored;
        private long _revisionsSubmitted;
        private long _filesVerified;

        public PullRun(VertexConnection vertex, IPullSource source, MirrorLog log, string work)
        {
            _vertex = vertex;
            _source = source;
            _log = log;
            _work = work;
            _sync = Path.Combine(work, "sync");
            _csvPath = Path.Combine(log.OutFolder, $"pull-{log.RunId}.csv");
            _evidence = Path.Combine(log.OutFolder, $"pull-{log.RunId}-evidence");
        }

        private sealed record LocalFile(string FullPath, string Relative, long Size, long Ticks, ChunkedFile Chunked, string Sha256);

        private sealed record SubmittedFile(string Path, string FileId);

        /// <returns>The number of problems found.</returns>
        public async Task<long> RunAsync(TimeSpan interval, TimeSpan? duration, string? start, int fullEvery, CancellationToken ct)
        {
            // A reused --work folder starts clean.
            WorkFolder.DeleteTree(_sync);
            Directory.CreateDirectory(_sync);

            // What the branch holds is the base of the first submit; its newest trailer says where the last run stopped.
            await ReloadSubmittedAsync(true, ct);
            string? resumeAt = start is null ? await FindResumePointAsync(ct) : null;
            await _source.OpenAsync(start, resumeAt, ct);
            _resumed = resumeAt is not null;
            string position = resumeAt is null ? $"Starting at {_source.Current.Label}" : $"Resuming at {_source.Current.Label}, the last one on {_vertex.Describe}";
            _log.Info($"{position}; {_source.Remaining:N0} more to mirror after it.");
            if (_submitted.Count > 0 && resumeAt is null)
                _log.Info($"{_vertex.Describe} already has {_submitted.Count:N0} file(s); the first revision brings it in line with {_source.Current.Label}.");

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (duration is TimeSpan limit) stop.CancelAfter(limit);
            CancellationToken token = stop.Token;

            File.AppendAllText(_csvPath, "utc,cycle,change,added,modified,deleted,uploaded_bytes,scan_ms,upload_ms,submit_ms,sync_ms,verify_ms,full_check,revision,problems\n");
            _log.Info($"Pulling {_source.Describe} into {_vertex.Describe} on {_vertex.Server}, one change every {Format.Duration(interval)}, " +
                      $"{(duration is null ? "until Ctrl+C" : $"for {Format.Duration(duration.Value)}")}. Full check every {fullEvery} cycle(s) and when caught up.");
            AnsiConsole.MarkupLine($"[grey]Per-cycle numbers: {Markup.Escape(_csvPath)}[/]");
            AnsiConsole.MarkupLine($"[grey]Problems:          {Markup.Escape(_log.FilePath)}[/]");

            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    Stopwatch cycle = Stopwatch.StartNew();
                    _cycle++;
                    await RunCycleAsync(fullEvery, token);
                    TimeSpan wait = (_caughtUp && interval < IdleInterval ? IdleInterval : interval) - cycle.Elapsed;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // --duration passed or Ctrl+C: a normal end for a run like this.
            }

            PrintSummary(clock.Elapsed);
            return _log.Problems;
        }

        /// <summary>
        /// The source id in the newest of the branch's recent revisions that has this source's trailer, or null when
        /// none has one (a new branch, or one this source never pulled into).
        /// </summary>
        private async Task<string?> FindResumePointAsync(CancellationToken ct)
        {
            if (_lastRevision == 0) return null;
            IReadOnlyList<RevisionSummary> recent = await _vertex.Revisions.GetRevisionsAsync(_vertex.ProjectId, _vertex.BranchId, Math.Max(0, _lastRevision - ResumeSearch), ResumeSearch, ct);
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                if (Trailer.Find(recent[i].Message, _source.TrailerName) is string id) return id;
            }

            return null;
        }

        private void PrintSummary(TimeSpan elapsed)
        {
            AnsiConsole.WriteLine();
            PhaseStats.Print([_cycleTimer, _scanTimer, _uploadTimer, _submitTimer, _syncTimer, _verifyTimer, _fullTimer, _fetchTimer]);
            string summary = $"Pulled {_changesMirrored:N0} change(s) from {_source.Describe} in {_cycleTimer.Summarize().Count:N0} cycle(s) over {Format.Duration(elapsed)}: " +
                             $"{_revisionsSubmitted:N0} revision(s), {_filesVerified:N0} file(s) verified, {_log.Problems:N0} problem(s). Last: {_source.Current.Label}, {_source.Remaining:N0} to go.";
            _log.Write(summary);
            AnsiConsole.MarkupLine(_log.Problems == 0 ? $"[green]{Markup.Escape(summary)}[/]" : $"[red]{Markup.Escape(summary)}[/] [grey]See {Markup.Escape(_log.FilePath)}.[/]");
        }

        /// <summary>
        /// Moves the source one change forward. A failure to reach the source (offline for a while) isn't a Vertex
        /// problem: it's logged, not counted, and the next cycle tries again.
        /// </summary>
        private async Task<bool> AdvanceAsync(CancellationToken ct)
        {
            Stopwatch timer = Stopwatch.StartNew();
            bool checking = _source.Remaining == 0;
            try
            {
                bool moved = await _source.AdvanceAsync(ct);
                if (checking) _fetchTimer.Record(timer.Elapsed);
                return moved;
            }
            catch (ToolFailedException ex)
            {
                _fetchTimer.Error();
                _log.Write($"Couldn't move {_source.Describe} forward (not counted as a problem): {ex.Message}");
                return false;
            }
        }

        private async Task RunCycleAsync(int fullEvery, CancellationToken ct)
        {
            Stopwatch total = Stopwatch.StartNew();
            _cycleProblems = 0;
            bool moved = _cycle == 1 || await AdvanceAsync(ct);
            if (moved && !(_cycle == 1 && _resumed)) _changesMirrored++;
            bool justCaughtUp = _source.Remaining == 0 && !_caughtUp;
            _caughtUp = _source.Remaining == 0;
            bool fullCheck = _cycle == 1 || _cycle % fullEvery == 0 || justCaughtUp;
            if (justCaughtUp) _log.Info($"Caught up with {_source.Describe} at {_source.Current.Label}. Checking for new changes every {Format.Duration(IdleInterval)}.");

            // Nothing new and no check due: nothing to do until the source moves.
            if (!moved && !fullCheck) return;
            string message = Message(_source.Current);

            // 1. Scan the clone, hashing only files whose size or write time changed.
            Stopwatch phase = Stopwatch.StartNew();
            Dictionary<string, LocalFile> expected = await Task.Run(() => Scan(ct), ct);
            TimeSpan scan = phase.Elapsed;
            _scanTimer.Record(scan);

            // 2. Diff against what the server holds and submit it as one revision, like 'vx submit'.
            List<FileChange> changes = new();
            List<ChunkedFile> content = new();
            int added = 0, modified = 0, deleted = 0;
            foreach ((string key, LocalFile file) in expected)
            {
                if (!_submitted.TryGetValue(key, out SubmittedFile? old))
                {
                    changes.Add(new FileChange { Path = file.Relative, FileId = file.Chunked.FileId, BaseFileId = string.Empty });
                    content.Add(file.Chunked);
                    added++;
                }
                else if (old.FileId != file.Chunked.FileId)
                {
                    changes.Add(new FileChange { Path = file.Relative, FileId = file.Chunked.FileId, BaseFileId = old.FileId });
                    content.Add(file.Chunked);
                    modified++;
                }
            }

            foreach ((string key, SubmittedFile old) in _submitted)
            {
                if (expected.ContainsKey(key)) continue;
                changes.Add(new FileChange { Path = old.Path, BaseFileId = old.FileId });
                deleted++;
            }

            TimeSpan upload = TimeSpan.Zero, submit = TimeSpan.Zero;
            long uploadedBytes = 0;
            long revision = _lastRevision;
            bool inStep = true;
            if (changes.Count > 0)
            {
                try
                {
                    phase.Restart();
                    ChunkedFile[] distinct = content.DistinctBy(c => c.FileId).ToArray();
                    HashSet<string> missing = await _vertex.Files.FindMissingFilesAsync(distinct.Select(c => c.FileId), ct);
                    ChunkedFile[] toSend = distinct.Where(c => missing.Contains(c.FileId)).ToArray();
                    if (toSend.Length > 0) uploadedBytes = (await _vertex.Files.UploadAsync(toSend, ct: ct)).RawBytesUploaded;
                    upload = phase.Elapsed;
                    _uploadTimer.Record(upload);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _uploadTimer.Error();
                    Problem($"Upload failed: {VertexConnection.DescribeError(ex)}");
                    inStep = false;
                }

                if (inStep)
                {
                    try
                    {
                        phase.Restart();
                        revision = await _vertex.Revisions.SubmitAsync(_vertex.ProjectId, _vertex.BranchId, _workspaceId, Host, message, changes, ct);
                        submit = phase.Elapsed;
                        _submitTimer.Record(submit);
                        _revisionsSubmitted++;
                        if (revision <= _lastRevision) Problem($"Submit returned revision #{revision}, not newer than the previous #{_lastRevision}.");
                        _lastRevision = revision;

                        Dictionary<string, SubmittedFile> next = new(StringComparer.Ordinal);
                        foreach ((string key, LocalFile file) in expected)
                        {
                            next[key] = _submitted.TryGetValue(key, out SubmittedFile? old) && old.FileId == file.Chunked.FileId
                                ? old
                                : new SubmittedFile(file.Relative, file.Chunked.FileId);
                        }

                        _submitted = next;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        _submitTimer.Error();
                        Problem($"Submit of {changes.Count:N0} file change(s) at {_source.Current.Label} failed: {VertexConnection.DescribeError(ex)}");
                        inStep = false;

                        // It may have committed even though the answer was lost: diff against the server from now on.
                        await ReloadSubmittedAsync(false, ct);
                    }
                }
            }

            _expected = expected;

            // 3. Sync the second folder like 'vx sync' and check what came back.
            phase.Restart();
            List<string> downloaded = await SyncAsync(ct);
            TimeSpan sync = phase.Elapsed;
            _syncTimer.Record(sync);

            phase.Restart();
            if (inStep) VerifySynced(downloaded, fullCheck);
            TimeSpan verify = phase.Elapsed;
            _verifyTimer.Record(verify);

            if (fullCheck && inStep) await FullCheckAsync(ct);

            _cycleTimer.Record(total.Elapsed);
            WriteCsv(added, modified, deleted, uploadedBytes, scan, upload, submit, sync, verify, fullCheck, revision);

            string change = !moved ? "no new changes" : $"+{added} ~{modified} -{deleted}, {Format.Bytes(uploadedBytes)} up";
            string status = _cycleProblems == 0 ? "[green]OK[/]" : $"[red]{_cycleProblems} problem(s), see the log[/]";
            AnsiConsole.MarkupLine($"[grey]{DateTime.Now:HH:mm:ss}[/] cycle {_cycle,-4} {Markup.Escape(_source.Current.Label),-20} #{revision,-6} ({change}) | " +
                                   $"scan {Format.Milliseconds(scan)} | upload {Format.Milliseconds(upload)} | submit {Format.Milliseconds(submit)} | " +
                                   $"sync {Format.Milliseconds(sync)} | verify {Format.Milliseconds(verify)} | " +
                                   $"{status}{(fullCheck && inStep ? " [grey](full check)[/]" : "")}");
        }

        /// <summary>
        /// The source folder's files by path key. Unchanged files (same size and write time) keep their hashes.
        /// </summary>
        private Dictionary<string, LocalFile> Scan(CancellationToken ct)
        {
            // Hidden files count too; symlinks and submodule links are skipped, like everything under .git.
            EnumerationOptions top = new() { AttributesToSkip = FileAttributes.ReparsePoint };
            EnumerationOptions deep = new() { AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = true };
            string root = _source.Root;
            List<string> paths = Directory.EnumerateFiles(root, "*", top).ToList();
            foreach (string directory in Directory.EnumerateDirectories(root, "*", top))
            {
                if (_source.SkipFolder is not null && string.Equals(Path.GetFileName(directory), _source.SkipFolder, StringComparison.OrdinalIgnoreCase)) continue;
                paths.AddRange(Directory.EnumerateFiles(directory, "*", deep));
            }

            Dictionary<string, LocalFile> local = new(StringComparer.Ordinal);
            List<(string Full, string Relative, FileInfo Info)> changed = new();
            foreach (string full in paths)
            {
                string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (!RepositoryPath.IsValid(relative))
                {
                    if (_reported.Add("invalid:" + relative)) _log.Write($"Skipped '{relative}': not a valid Vertex path.");
                    continue;
                }

                FileInfo info = new(full);
                if (_local.TryGetValue(relative, out LocalFile? known) && known.Size == info.Length && known.Ticks == info.LastWriteTimeUtc.Ticks) local[relative] = known;
                else changed.Add((full, relative, info));
            }

            foreach (LocalFile file in changed.AsParallel().WithCancellation(ct).Select(c => Hash(c.Full, c.Relative, c.Info)).ToArray())
                local[file.Relative] = file;
            _local = local;

            // Vertex compares paths ignoring case; a source with two spellings of one path keeps the first.
            Dictionary<string, LocalFile> byKey = new(StringComparer.Ordinal);
            foreach (LocalFile file in local.Values.OrderBy(f => f.Relative, StringComparer.Ordinal))
            {
                if (!byKey.TryAdd(RepositoryPath.ToKey(file.Relative), file) && _reported.Add("case:" + file.Relative))
                    _log.Write($"Skipped '{file.Relative}': differs only in case from another file.");
            }

            return byKey;
        }

        private static LocalFile Hash(string full, string relative, FileInfo info)
        {
            long size = info.Length;
            long ticks = info.LastWriteTimeUtc.Ticks;
            return new LocalFile(full, relative, size, ticks, ContentChunker.ChunkFile(full), Sha256(full));
        }

        /// <summary>
        /// Applies the branch's changes since the last sync to the sync folder, checking each listed file id is the
        /// one that was submitted.
        /// </summary>
        /// <returns>Relative paths downloaded.</returns>
        private async Task<List<string>> SyncAsync(CancellationToken ct)
        {
            HeadChanges delta;
            try
            {
                delta = await _vertex.Revisions.GetHeadChangesAsync(_vertex.ProjectId, _vertex.BranchId, _syncRevision, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _syncTimer.Error();
                Problem($"Sync listing after #{_syncRevision} failed: {VertexConnection.DescribeError(ex)}");
                return [];
            }

            List<(string Relative, string FileId)> downloads = new();
            foreach (HeadChange change in delta.Files)
            {
                string relative = change.Path;
                _submitted.TryGetValue(RepositoryPath.ToKey(relative), out SubmittedFile? expected);
                if (change.FileId is null)
                {
                    if (expected is not null) Problem($"The server lists '{relative}' as deleted, but it was submitted as {expected.FileId}.");
                    string gone = SyncPath(relative);
                    if (File.Exists(gone)) File.Delete(gone);
                    continue;
                }

                if (expected is null) Problem($"The server lists '{relative}' at {change.FileId}, but it was never submitted or was deleted.");
                else if (expected.FileId != change.FileId) Problem($"The server lists '{relative}' at {change.FileId}, but {expected.FileId} was submitted.");
                downloads.Add((relative, change.FileId));
            }

            int failed = 0;
            await Parallel.ForEachAsync(downloads, new ParallelOptions { MaxDegreeOfParallelism = DownloadParallelism, CancellationToken = ct }, async (download, token) =>
            {
                try
                {
                    await _vertex.Files.DownloadAsync(download.FileId, SyncPath(download.Relative), token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref failed);
                    _syncTimer.Error();
                    Problem($"Download of '{download.Relative}' ({download.FileId}) failed: {VertexConnection.DescribeError(ex)}");
                }
            });

            // Only a complete sync moves on; otherwise the next cycle fetches the same changes again.
            if (failed == 0) _syncRevision = delta.HeadRevision;
            return downloads.Select(d => d.Relative).ToList();
        }

        /// <summary>
        /// The sync folder must hold exactly the source's files: same set, same sizes, and the same bytes for every file
        /// downloaded this cycle (every file on a full check).
        /// </summary>
        private void VerifySynced(List<string> downloaded, bool everything)
        {
            Dictionary<string, string> synced = new(StringComparer.Ordinal);
            foreach (string full in Directory.EnumerateFiles(_sync, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                synced[RepositoryPath.ToKey(Path.GetRelativePath(_sync, full).Replace('\\', '/'))] = full;

            foreach ((string key, LocalFile file) in _expected)
            {
                if (!synced.TryGetValue(key, out string? full))
                {
                    Problem($"'{file.Relative}' is missing after sync.");
                    continue;
                }

                long size = new FileInfo(full).Length;
                if (size != file.Size) Problem($"'{file.Relative}' synced as {size:N0} bytes, expected {file.Size:N0}.", full, file);
            }

            foreach ((string key, string full) in synced)
            {
                if (!_expected.ContainsKey(key)) Problem($"'{Path.GetRelativePath(_sync, full)}' is in the synced folder but not in the source (a delete didn't sync).");
            }

            IEnumerable<string> keys = everything ? _expected.Keys : downloaded.Select(RepositoryPath.ToKey).Where(_expected.ContainsKey);
            foreach (string key in keys.Distinct())
            {
                if (!synced.TryGetValue(key, out string? full)) continue;
                LocalFile file = _expected[key];
                _filesVerified++;
                if (!string.Equals(Sha256(full), file.Sha256, StringComparison.Ordinal))
                    Problem($"'{file.Relative}' synced with different content (SHA-256 mismatch).", full, file);
            }
        }

        /// <summary>
        /// The whole branch listing must match what was submitted, and a fresh download of every file must match the source.
        /// </summary>
        private async Task FullCheckAsync(CancellationToken ct)
        {
            Stopwatch timer = Stopwatch.StartNew();
            HeadChanges all;
            try
            {
                all = await _vertex.Revisions.GetHeadChangesAsync(_vertex.ProjectId, _vertex.BranchId, 0, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _fullTimer.Error();
                Problem($"Full listing failed: {VertexConnection.DescribeError(ex)}");
                return;
            }

            Dictionary<string, HeadChange> listed = all.Files.Where(f => f.FileId is not null)
                .ToDictionary(f => RepositoryPath.ToKey(f.Path), StringComparer.Ordinal);
            foreach ((string key, LocalFile file) in _expected)
            {
                if (!listed.TryGetValue(key, out HeadChange? entry)) Problem($"Full listing is missing '{file.Relative}'.");
                else if (entry.FileId != file.Chunked.FileId) Problem($"Full listing has '{file.Relative}' at {entry.FileId}, expected {file.Chunked.FileId}.");
            }

            foreach ((string key, HeadChange entry) in listed)
            {
                if (!_expected.ContainsKey(key)) Problem($"Full listing has '{entry.Path}', which isn't in the source.");
            }

            // A fresh workspace: download everything again and compare bytes.
            string fresh = Path.Combine(_work, "fresh");
            WorkFolder.DeleteTree(fresh);
            Directory.CreateDirectory(fresh);
            try
            {
                await Parallel.ForEachAsync(_expected.Values, new ParallelOptions { MaxDegreeOfParallelism = DownloadParallelism, CancellationToken = ct }, async (file, token) =>
                {
                    string target = Path.Combine(fresh, file.Relative.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        await _vertex.Files.DownloadAsync(file.Chunked.FileId, target, token);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        _fullTimer.Error();
                        Problem($"Fresh download of '{file.Relative}' failed: {VertexConnection.DescribeError(ex)}");
                        return;
                    }

                    Interlocked.Increment(ref _filesVerified);
                    if (!string.Equals(Sha256(target), file.Sha256, StringComparison.Ordinal))
                        Problem($"'{file.Relative}' downloaded fresh with different content (SHA-256 mismatch).", target, file);
                });
            }
            finally
            {
                WorkFolder.DeleteTree(fresh);
            }

            _fullTimer.Record(timer.Elapsed);
        }

        /// <summary>
        /// Rebuilds what the branch holds from a full listing: at start (so a reused --project continues from its
        /// current state; a failure there stops the run), and after a submit whose outcome is unknown.
        /// </summary>
        private async Task ReloadSubmittedAsync(bool required, CancellationToken ct)
        {
            try
            {
                HeadChanges all = await _vertex.Revisions.GetHeadChangesAsync(_vertex.ProjectId, _vertex.BranchId, 0, ct);
                _submitted = all.Files.Where(f => f.FileId is not null)
                    .ToDictionary(f => RepositoryPath.ToKey(f.Path), f => new SubmittedFile(f.Path, f.FileId!), StringComparer.Ordinal);
                _lastRevision = Math.Max(_lastRevision, all.HeadRevision);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && !required)
            {
                _log.Write($"Couldn't read the branch's files: {ex.Message}");
            }
        }

        /// <summary>
        /// Records a problem: the console line, the log, and a copy of the bad file next to the source's.
        /// </summary>
        private void Problem(string message, string? badFile = null, LocalFile? expected = null)
        {
            Interlocked.Increment(ref _cycleProblems);
            string detail = $"(cycle {_cycle} at {_source.Current.Label})";
            if (badFile is not null && expected is not null)
            {
                try
                {
                    string folder = Path.Combine(_evidence, $"cycle-{_cycle}");
                    string copy = Path.Combine(folder, expected.Relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(badFile, copy + ".vertex", true);
                    File.Copy(expected.FullPath, copy + ".source", true);
                    detail += $" Kept both copies in {folder}.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    detail += $" (Couldn't keep a copy: {ex.Message})";
                }
            }

            _log.Problem(message, detail);
        }

        private void WriteCsv(int added, int modified, int deleted, long uploadedBytes, TimeSpan scan, TimeSpan upload, TimeSpan submit, TimeSpan sync, TimeSpan verify,
            bool fullCheck, long revision)
        {
            string row = string.Join(',',
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                _cycle.ToString(CultureInfo.InvariantCulture),
                _source.Current.Id,
                added.ToString(CultureInfo.InvariantCulture),
                modified.ToString(CultureInfo.InvariantCulture),
                deleted.ToString(CultureInfo.InvariantCulture),
                uploadedBytes.ToString(CultureInfo.InvariantCulture),
                scan.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                upload.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                submit.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                sync.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                verify.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                fullCheck ? "1" : "0",
                revision.ToString(CultureInfo.InvariantCulture),
                _cycleProblems.ToString(CultureInfo.InvariantCulture));
            File.AppendAllText(_csvPath, row + "\n");
        }

        /// <summary>
        /// The revision message: the change's own message as written, then who made it and when (the revision's
        /// submitter is the mirror's login), and the trailer a rerun resumes from. The server refuses an empty message,
        /// so a change without one is described by its label.
        /// </summary>
        private string Message(SourceChange change)
        {
            string body = change.Message.Trim().Length == 0 ? change.Label : change.Message;
            return Trailer.Append(body, _source.TrailerName, change.Id, $"Authored by {change.Author} at {change.Time.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC.");
        }

        private string SyncPath(string relative)
        {
            return Path.Combine(_sync, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string Sha256(string path)
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
