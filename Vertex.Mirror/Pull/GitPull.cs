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
    /// git -> Vertex: replays a git repository into a Vertex branch one commit per interval, syncs every revision back
    /// into a second folder the way <c>vx sync</c> does, and compares every byte with git's copy. Runs until the
    /// duration passes or Ctrl+C, so it can run all day and show whether anything was ever corrupted or got slow.
    /// </summary>
    /// <remarks>
    /// The branch is the mirror: files go at the repository's own paths, each commit is one revision with the commit's
    /// raw message, and anything on the branch that isn't in the repository is deleted by the first revision. It starts
    /// --replay commits behind the repository's head (or at --start) so every cycle has a real change to submit even
    /// when the upstream is quiet, and fetches new upstream commits once it catches up. Every cycle is one line on the
    /// console and one row in <c>pull-&lt;run&gt;.csv</c>; every problem goes to <c>pull-&lt;run&gt;.log</c>, and files that
    /// came back different are kept under <c>pull-&lt;run&gt;-evidence/</c>.
    /// </remarks>
    public sealed class GitPull
    {
        private const int DownloadParallelism = 8;
        private const string Host = "vx-mirror";

        private readonly VertexConnection _vertex;
        private readonly MirrorLog _log;
        private readonly string _repository;
        private readonly string _work;
        private readonly string _clone;
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
        private readonly PhaseStats _fetchTimer = new("git fetch");

        // The clone as last scanned (by relative path), and what the server holds for it (by path key).
        private Dictionary<string, LocalFile> _local = new(StringComparer.Ordinal);
        private Dictionary<string, LocalFile> _expected = new(StringComparer.Ordinal);
        private Dictionary<string, SubmittedFile> _submitted = new(StringComparer.Ordinal);
        private string _branch = string.Empty;
        private string _commit = string.Empty;

        /// <summary>The current commit's position on the branch's first-parent line, 1 for the first commit.</summary>
        private int _commitNumber;

        private long _syncRevision;
        private long _lastRevision;
        private int _cycle;
        private int _cycleProblems;
        private long _commitsMirrored;
        private long _revisionsSubmitted;
        private long _filesVerified;

        public GitPull(VertexConnection vertex, MirrorLog log, string repository, string work)
        {
            _vertex = vertex;
            _log = log;
            _repository = repository;
            _work = work;
            _clone = Path.Combine(work, "git");
            _sync = Path.Combine(work, "sync");
            _csvPath = Path.Combine(log.OutFolder, $"pull-{log.RunId}.csv");
            _evidence = Path.Combine(log.OutFolder, $"pull-{log.RunId}-evidence");
        }

        private sealed record LocalFile(string FullPath, string Relative, long Size, long Ticks, ChunkedFile Chunked, string Sha256);

        private sealed record SubmittedFile(string Path, string FileId);

        /// <returns>The number of problems found.</returns>
        public async Task<long> RunAsync(TimeSpan interval, TimeSpan? duration, string? start, int replay, int fullEvery, CancellationToken ct)
        {
            // A reused --work folder starts clean.
            WorkFolder.DeleteTree(_clone);
            WorkFolder.DeleteTree(_sync);
            Directory.CreateDirectory(_sync);
            await CloneAsync(start, replay, ct);

            // The branch is the mirror: whatever it already holds is the base of the first submit, and anything on it
            // that isn't in the repository gets deleted, like a 'git push --force' of the starting commit.
            await ReloadSubmittedAsync(ct);
            if (_submitted.Count > 0)
                _log.Info($"{_vertex.Describe} already has {_submitted.Count:N0} file(s); the first revision brings it in line with the starting commit.");

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (duration is TimeSpan limit) stop.CancelAfter(limit);
            CancellationToken token = stop.Token;

            File.AppendAllText(_csvPath, "utc,cycle,commit_number,commit,added,modified,deleted,uploaded_bytes,scan_ms,upload_ms,submit_ms,sync_ms,verify_ms,full_check,revision,problems\n");
            _log.Info($"Pulling {_repository} into {_vertex.Describe} on {_vertex.Server} every {Format.Duration(interval)}, " +
                      $"{(duration is null ? "until Ctrl+C" : $"for {Format.Duration(duration.Value)}")}. Full check every {fullEvery} cycle(s).");
            AnsiConsole.MarkupLine($"[grey]Per-cycle numbers: {Markup.Escape(_csvPath)}[/]");
            AnsiConsole.MarkupLine($"[grey]Problems:          {Markup.Escape(_log.FilePath)}[/]");

            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    Stopwatch cycle = Stopwatch.StartNew();
                    _cycle++;
                    await RunCycleAsync(_cycle == 1 || _cycle % fullEvery == 0, token);
                    TimeSpan wait = interval - cycle.Elapsed;
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

        private void PrintSummary(TimeSpan elapsed)
        {
            AnsiConsole.WriteLine();
            PhaseStats.Print([_cycleTimer, _scanTimer, _uploadTimer, _submitTimer, _syncTimer, _verifyTimer, _fullTimer, _fetchTimer]);
            string summary = $"Pulled {_commitsMirrored:N0} commit(s) from {_repository} ({_branch}) in {_cycleTimer.Summarize().Count:N0} cycle(s) over {Format.Duration(elapsed)}: " +
                             $"{_revisionsSubmitted:N0} revision(s), {_filesVerified:N0} file(s) verified, {_log.Problems:N0} problem(s). Last commit {_commitNumber:N0} {Format.ShortSha(_commit)}.";
            _log.Write(summary);
            AnsiConsole.MarkupLine(_log.Problems == 0 ? $"[green]{Markup.Escape(summary)}[/]" : $"[red]{Markup.Escape(summary)}[/] [grey]See {Markup.Escape(_log.FilePath)}.[/]");
        }

        /// <summary>
        /// Clones the repository and checks out the first commit to mirror: <paramref name="start"/> when given (a
        /// commit number, 1 for the first commit, or a sha/tag on the branch), otherwise <paramref name="replay"/>
        /// first-parent commits before its head.
        /// </summary>
        private async Task CloneAsync(string? start, int replay, CancellationToken ct)
        {
            AnsiConsole.MarkupLine($"[grey]Cloning {Markup.Escape(_repository)}...[/]");
            try
            {
                await Git.RunAsync(_work, ct, "clone", "--quiet", "--no-checkout", "--single-branch", _repository, _clone);
            }
            catch (ToolFailedException ex)
            {
                throw new MirrorSetupException($"Couldn't clone {_repository}: {ex.Message}. Check the address, or pass another --git.");
            }

            Git.DisableConversions(_clone);
            _branch = await Git.RunAsync(_clone, ct, "symbolic-ref", "--short", "HEAD");
            // The branch's first-parent line, oldest first: commit N is history[N - 1].
            string[] history;
            try
            {
                history = Format.Lines(await Git.RunAsync(_clone, ct, "rev-list", "--first-parent", "--reverse", "HEAD"));
            }
            catch (ToolFailedException)
            {
                throw new MirrorSetupException($"{_repository} has no commits yet.");
            }

            int index;
            if (start is null)
            {
                index = Math.Max(0, history.Length - 1 - replay);
            }
            else if (start.Length < 7 && int.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                if (number < 1 || number > history.Length)
                    throw new MirrorSetupException($"--start {number} is past the end: {_branch} has {history.Length:N0} commit(s). Use a number from 1 to {history.Length:N0}.");
                index = number - 1;
            }
            else
            {
                string sha;
                try
                {
                    sha = await Git.RunAsync(_clone, ct, "rev-parse", "--verify", "--quiet", $"{start}^{{commit}}");
                }
                catch (ToolFailedException)
                {
                    throw new MirrorSetupException($"--start '{start}' isn't a commit in {_repository}. Use a commit number (1 = the first commit), a sha or a tag.");
                }

                index = Array.IndexOf(history, sha);
                if (index < 0)
                    throw new MirrorSetupException($"--start '{start}' isn't on {_branch}'s main line (it may be inside a merged branch). Use a commit number instead, from 1 to {history.Length:N0}.");
            }

            _commit = history[index];
            _commitNumber = index + 1;
            await Git.RunAsync(_clone, ct, "checkout", "--quiet", "--force", "--detach", _commit);
            _log.Info($"Starting at commit {_commitNumber:N0} of {history.Length:N0} on {_branch} ({Format.ShortSha(_commit)}), {history.Length - _commitNumber:N0} to replay.");
        }

        /// <summary>
        /// Moves the clone one commit forward, fetching new upstream commits once the replay caught up.
        /// </summary>
        /// <returns>True when the clone moved to a new commit.</returns>
        private async Task<bool> AdvanceAsync(CancellationToken ct)
        {
            string[] ahead = Format.Lines(await Git.RunAsync(_clone, ct, "rev-list", "--first-parent", "--reverse", $"{_commit}..origin/{_branch}"));
            if (ahead.Length == 0)
            {
                Stopwatch fetch = Stopwatch.StartNew();
                try
                {
                    await Git.RunAsync(_clone, ct, "fetch", "--quiet", "origin");
                    _fetchTimer.Record(fetch.Elapsed);
                }
                catch (ToolFailedException ex)
                {
                    // Offline for a while is not a Vertex problem; mirror what we have and try again next cycle.
                    _fetchTimer.Error();
                    _log.Write($"git fetch failed (not counted as a problem): {ex.Message}");
                }

                ahead = Format.Lines(await Git.RunAsync(_clone, ct, "rev-list", "--first-parent", "--reverse", $"{_commit}..origin/{_branch}"));
                if (ahead.Length == 0) return false;
            }

            await Git.RunAsync(_clone, ct, "checkout", "--quiet", "--force", "--detach", ahead[0]);
            _commit = ahead[0];
            _commitNumber++;
            return true;
        }

        private async Task RunCycleAsync(bool fullCheck, CancellationToken ct)
        {
            Stopwatch total = Stopwatch.StartNew();
            _cycleProblems = 0;
            bool moved = _cycle == 1 || await AdvanceAsync(ct);
            if (moved) _commitsMirrored++;
            string message = await CommitMessageAsync(ct);

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
                        Problem($"Submit of {changes.Count:N0} change(s) at {Format.ShortSha(_commit)} failed: {VertexConnection.DescribeError(ex)}");
                        inStep = false;

                        // It may have committed even though the answer was lost: diff against the server from now on.
                        await ReloadSubmittedAsync(ct);
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

            string change = !moved ? "no new commits" : $"+{added} ~{modified} -{deleted}, {Format.Bytes(uploadedBytes)} up";
            string status = _cycleProblems == 0 ? "[green]OK[/]" : $"[red]{_cycleProblems} problem(s), see the log[/]";
            AnsiConsole.MarkupLine($"[grey]{DateTime.Now:HH:mm:ss}[/] cycle {_cycle,-4} commit {_commitNumber,-5} {Format.ShortSha(_commit)} #{revision,-6} ({change}) | " +
                                   $"scan {Format.Milliseconds(scan)} | upload {Format.Milliseconds(upload)} | submit {Format.Milliseconds(submit)} | " +
                                   $"sync {Format.Milliseconds(sync)} | verify {Format.Milliseconds(verify)} | " +
                                   $"{status}{(fullCheck && inStep ? " [grey](full check)[/]" : "")}");
        }

        /// <summary>
        /// The clone's files by path key. Unchanged files (same size and write time) keep their hashes.
        /// </summary>
        private Dictionary<string, LocalFile> Scan(CancellationToken ct)
        {
            // Hidden files count too; symlinks and submodule links are skipped, like everything under .git.
            EnumerationOptions top = new() { AttributesToSkip = FileAttributes.ReparsePoint };
            EnumerationOptions deep = new() { AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = true };
            List<string> paths = Directory.EnumerateFiles(_clone, "*", top).ToList();
            foreach (string directory in Directory.EnumerateDirectories(_clone, "*", top))
            {
                if (Path.GetFileName(directory) == ".git") continue;
                paths.AddRange(Directory.EnumerateFiles(directory, "*", deep));
            }

            Dictionary<string, LocalFile> local = new(StringComparer.Ordinal);
            List<(string Full, string Relative, FileInfo Info)> changed = new();
            foreach (string full in paths)
            {
                string relative = Path.GetRelativePath(_clone, full).Replace('\\', '/');
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

            // Vertex compares paths ignoring case; a repository with two spellings of one path keeps the first.
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
        /// The sync folder must hold exactly the clone's files: same set, same sizes, and the same bytes for every file
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
                if (!_expected.ContainsKey(key)) Problem($"'{Path.GetRelativePath(_sync, full)}' is in the synced folder but not in the repository (a delete didn't sync).");
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
        /// The whole branch listing must match what was submitted, and a fresh download of every file must match git.
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
                if (!_expected.ContainsKey(key)) Problem($"Full listing has '{entry.Path}', which isn't in the repository.");
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
        /// current state), and after a submit whose outcome is unknown.
        /// </summary>
        private async Task ReloadSubmittedAsync(CancellationToken ct)
        {
            try
            {
                HeadChanges all = await _vertex.Revisions.GetHeadChangesAsync(_vertex.ProjectId, _vertex.BranchId, 0, ct);
                _submitted = all.Files.Where(f => f.FileId is not null)
                    .ToDictionary(f => RepositoryPath.ToKey(f.Path), f => new SubmittedFile(f.Path, f.FileId!), StringComparer.Ordinal);
                _lastRevision = Math.Max(_lastRevision, all.HeadRevision);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.Write($"Couldn't read the branch's files: {ex.Message}");
            }
        }

        /// <summary>
        /// Records a problem: the console line, the log, and a copy of the bad file next to git's.
        /// </summary>
        private void Problem(string message, string? badFile = null, LocalFile? expected = null)
        {
            Interlocked.Increment(ref _cycleProblems);
            string detail = $"(cycle {_cycle} at {Format.ShortSha(_commit)})";
            if (badFile is not null && expected is not null)
            {
                try
                {
                    string folder = Path.Combine(_evidence, $"cycle-{_cycle}");
                    string copy = Path.Combine(folder, expected.Relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(badFile, copy + ".vertex", true);
                    File.Copy(expected.FullPath, copy + ".git", true);
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
                _commitNumber.ToString(CultureInfo.InvariantCulture),
                Format.ShortSha(_commit),
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
        /// The commit's raw message, exactly as 'vx submit -m' would get it. The server refuses an empty message, so
        /// a commit made with --allow-empty-message is described by its sha instead.
        /// </summary>
        private async Task<string> CommitMessageAsync(CancellationToken ct)
        {
            string message = await Git.RunAsync(_clone, ct, "log", "-1", "--format=%B", _commit);
            return message.Length == 0 ? Format.ShortSha(_commit) : message;
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
