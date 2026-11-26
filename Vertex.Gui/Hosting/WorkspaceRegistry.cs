// Copyright 2026 Entex Interactive

using System.Text.Json;
using Vertex.Core.Client.Auth;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Net;

namespace Vertex.Gui.Hosting
{
    /// <summary>
    /// A workspace the app opened before, newest first in <see cref="WorkspaceRegistry.Recent"/>.
    /// </summary>
    /// <param name="LastOpened">Unix milliseconds.</param>
    public sealed record RecentWorkspace(string Root, string? Project, string? Branch, long LastOpened);

    /// <summary>
    /// The workspace the app has open, with the <see cref="WorkspaceMonitor"/> that locks files as they're saved, and
    /// the list of workspaces opened before (<c>workspaces.json</c> next to the CLI's credentials).
    /// </summary>
    /// <remarks>
    /// Everything that reads or writes the workspace index goes through <see cref="ExclusiveAsync{T}"/>, which pauses
    /// the monitor, so the index and the lock calls have one writer. Monitor results reach the UI as host events:
    /// <c>workspace.changed</c> (reload the changes), <c>workspace.lockConflict</c>, <c>workspace.activity</c> and
    /// <c>workspace.problem</c>.
    /// </remarks>
    public sealed class WorkspaceRegistry(ServerSession session, HostEvents events, ILogger<WorkspaceRegistry> logger) : IAsyncDisposable
    {
        private const int MaxRecent = 20;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        /// <summary>Quiet time after a monitor check before <c>workspace.changed</c> is sent, so a burst of saves is one reload.</summary>
        private static readonly TimeSpan ChangedDebounce = TimeSpan.FromMilliseconds(300);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Lock _changedLock = new();
        private LinkedWorkspace? _current;
        private WorkspaceMonitor? _monitor;
        private string? _monitorUser;
        private CancellationTokenSource? _changedDelay;
        private bool _restored;

        private static string RecentPath => Path.Combine(CredentialStore.Directory, "workspaces.json");

        /// <summary>
        /// The open workspace. The first call reopens the most recent one that still exists.
        /// </summary>
        public async Task<LinkedWorkspace?> CurrentAsync(CancellationToken ct)
        {
            if (_current is not null || _restored) return _current;

            await _gate.WaitAsync(ct);
            try
            {
                if (_restored) return _current;
                _restored = true;
                foreach (RecentWorkspace recent in Recent())
                {
                    if (!Directory.Exists(recent.Root)) continue;
                    VWorkspace? workspace = VWorkspace.Find(recent.Root);
                    if (workspace is null || LinkedWorkspace.TryFrom(workspace) is not LinkedWorkspace linked) continue;
                    await SwitchToAsync(linked);
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Couldn't reopen the last workspace");
            }
            finally
            {
                _gate.Release();
            }

            return _current;
        }

        /// <summary>
        /// The open workspace.
        /// </summary>
        /// <exception cref="HostProblemException">No workspace is open.</exception>
        public async Task<LinkedWorkspace> RequiredAsync(CancellationToken ct)
        {
            return await CurrentAsync(ct) ?? throw new HostProblemException("Open a workspace first: clone a project, or add a folder that has one.", StatusCodes.Status409Conflict);
        }

        /// <summary>
        /// Workspaces opened before, newest first.
        /// </summary>
        public IReadOnlyList<RecentWorkspace> Recent()
        {
            try
            {
                if (!File.Exists(RecentPath)) return [];
                return JsonSerializer.Deserialize<List<RecentWorkspace>>(File.ReadAllText(RecentPath), Json) ?? [];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Couldn't read {Path}", RecentPath);
                return [];
            }
        }

        /// <summary>
        /// Opens the workspace containing <paramref name="path"/> and makes it the current one.
        /// </summary>
        /// <exception cref="HostProblemException">The folder isn't a linked workspace.</exception>
        public async Task<LinkedWorkspace> OpenAsync(string path, CancellationToken ct)
        {
            string full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) throw new HostProblemException($"'{full}' doesn't exist. Pick another folder.", StatusCodes.Status404NotFound);

            VWorkspace? workspace = VWorkspace.Find(full);
            if (workspace is null)
                throw new HostProblemException($"'{full}' isn't a Vertex workspace. Clone a project into it, or create a new project there.", StatusCodes.Status409Conflict);
            if (LinkedWorkspace.TryFrom(workspace) is not LinkedWorkspace linked)
                throw new HostProblemException($"'{workspace.Root}' isn't linked to a project. Clone a project into it.", StatusCodes.Status409Conflict);

            await _gate.WaitAsync(ct);
            try
            {
                _restored = true;
                await SwitchToAsync(linked);
            }
            finally
            {
                _gate.Release();
            }

            return linked;
        }

        /// <summary>
        /// Removes a workspace from the recent list. Its files are left alone. Closes it when it's open.
        /// </summary>
        public async Task ForgetAsync(string root, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                SaveRecent(Recent().Where(r => !WorkspaceScanner.PathComparer.Equals(r.Root, root)).ToList());
                if (_current is not null && WorkspaceScanner.PathComparer.Equals(_current.Workspace.Root, root))
                {
                    await StopMonitorAsync();
                    _current = null;
                    events.Publish("workspace.opened", null);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// How operations on <paramref name="linked"/> reach the server: the workspace's pinned server or the app's,
        /// with the saved login for it.
        /// </summary>
        /// <exception cref="NotLoggedInException">No login is saved for that server.</exception>
        public WorkspaceConnection Connection(LinkedWorkspace linked)
        {
            string server = ServerFor(linked);
            Credential credential = CredentialStore.Get(server) ?? throw new NotLoggedInException(server);
            return new WorkspaceConnection(server, credential.Token, credential.Username, Environment.ProcessorCount);
        }

        /// <summary>
        /// The server a workspace talks to.
        /// </summary>
        public string ServerFor(LinkedWorkspace linked)
        {
            return string.IsNullOrWhiteSpace(linked.Workspace.Config.Address) ? session.Server : linked.Workspace.Config.Address.Trim();
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the open workspace with the monitor paused. When the work switched branches,
        /// the workspace is reloaded and the monitor follows the new branch.
        /// </summary>
        public async Task<T> ExclusiveAsync<T>(Func<LinkedWorkspace, Task<T>> work, CancellationToken ct)
        {
            LinkedWorkspace linked = await RequiredAsync(ct);
            await _gate.WaitAsync(ct);
            WorkspaceMonitor? paused = null;
            try
            {
                linked = _current ?? linked;
                if (_monitor is not null)
                {
                    await _monitor.PauseAsync(ct);
                    paused = _monitor;
                }

                try
                {
                    return await work(linked);
                }
                finally
                {
                    paused?.Resume();
                    paused = null;

                    // A checkout rewrites the config; follow it.
                    VWorkspace reloaded = VWorkspace.Find(linked.Workspace.Root) ?? linked.Workspace;
                    if (LinkedWorkspace.TryFrom(reloaded) is LinkedWorkspace now && now.BranchId != linked.BranchId) await SwitchToAsync(now);
                    else EnsureMonitor();
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopMonitorAsync();
            _gate.Dispose();
        }

        /// <summary>Call with the gate held.</summary>
        private async Task SwitchToAsync(LinkedWorkspace linked)
        {
            await StopMonitorAsync();
            _current = linked;

            List<RecentWorkspace> recent = Recent().Where(r => !WorkspaceScanner.PathComparer.Equals(r.Root, linked.Workspace.Root)).ToList();
            recent.Insert(0, new RecentWorkspace(linked.Workspace.Root, linked.Workspace.Config.Project, linked.Workspace.Config.Branch, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            SaveRecent(recent.Take(MaxRecent).ToList());

            EnsureMonitor();
            events.Publish("workspace.opened", new { root = linked.Workspace.Root });
        }

        /// <summary>
        /// Starts the monitor when a workspace is open and logged in, or restarts it after logging in as someone else.
        /// Call with the gate held.
        /// </summary>
        private void EnsureMonitor()
        {
            if (_current is not LinkedWorkspace linked) return;
            string server = ServerFor(linked);
            Credential? credential = CredentialStore.Get(server);
            if (_monitor is not null && credential?.Username == _monitorUser) return;
            if (_monitor is not null)
            {
                // Logged out or in as someone else: the old monitor's locks belong to the old login.
                WorkspaceMonitor old = _monitor;
                _monitor = null;
                _ = old.DisposeAsync().AsTask();
            }

            if (credential is null) return;

            WorkspaceMonitor monitor = new(linked.Workspace, server, () => CredentialStore.Get(server)?.Token, linked.ProjectId, linked.BranchId, linked.WorkspaceId, credential.Username);
            monitor.FilesChecked += _ => PublishChanged();
            monitor.LockConflict += held => events.Publish("workspace.lockConflict", new { held.Path, held.Username, held.Host, held.SameUser });
            monitor.Activity += item =>
            {
                events.Publish("workspace.activity", new
                {
                    kind = item.Kind.ToString(),
                    item.Username,
                    item.Host,
                    item.SameUser,
                    paths = item.Paths.Take(50).ToList(),
                    pathCount = item.Paths.Count,
                    item.Revision,
                    item.Message,
                    item.Files,
                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });

                // Someone's lock or revision changes what the changes list shows (lock badges, "new on the server").
                PublishChanged();
            };

            monitor.ProblemChanged += problem => events.Publish("workspace.problem", new
            {
                message = problem is null ? null : ServerErrorHandler.Describe(problem, session).Detail
            });

            monitor.Start();
            _monitor = monitor;
            _monitorUser = credential.Username;
        }

        private async Task StopMonitorAsync()
        {
            if (_monitor is null) return;
            WorkspaceMonitor monitor = _monitor;
            _monitor = null;
            _monitorUser = null;
            await monitor.DisposeAsync();
        }

        /// <summary>
        /// Tells the UI to reload the changes, once per burst.
        /// </summary>
        public void PublishChanged()
        {
            CancellationTokenSource delay = new();
            lock (_changedLock)
            {
                _changedDelay?.Cancel();
                _changedDelay = delay;
            }

            _ = Task.Delay(ChangedDebounce, delay.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) events.Publish("workspace.changed", null);
            }, TaskScheduler.Default);
        }

        private void SaveRecent(List<RecentWorkspace> recent)
        {
            try
            {
                Directory.CreateDirectory(CredentialStore.Directory);
                File.WriteAllText(RecentPath, JsonSerializer.Serialize(recent, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Couldn't save {Path}", RecentPath);
            }
        }
    }
}
