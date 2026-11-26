// Copyright 2026 Entex Interactive

using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Vertex.Core.Client.IO;
using Vertex.Core.Client.IO.IgnoreTemplates;
using Vertex.Core.Client.Workspaces;
using Vertex.Core.Data;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;
using Vertex.Core.Storage;
using Vertex.Gui.Hosting;

namespace Vertex.Gui.Controllers
{
    /// <param name="Root">The workspace folder.</param>
    /// <param name="Name">The folder's name, for pickers.</param>
    /// <param name="Server">The server it talks to.</param>
    public sealed record WorkspaceDto(string Root, string Name, string Project, Guid ProjectId, string Branch, Guid BranchId, string Server);

    /// <param name="Exists">False when the folder is gone or no longer a workspace.</param>
    /// <param name="LastOpened">Unix milliseconds.</param>
    public sealed record RecentWorkspaceDto(string Root, string Name, string? Project, string? Branch, long LastOpened, bool Exists);

    /// <param name="Current">The open workspace, or null.</param>
    public sealed record WorkspacesDto(WorkspaceDto? Current, IReadOnlyList<RecentWorkspaceDto> Recent);

    public enum ChangeKind
    {
        Added,
        Modified,
        Deleted,
        Moved
    }

    /// <param name="SameUser">The same user, in another workspace or on another machine.</param>
    public sealed record LockHolderDto(string Username, string Host, bool SameUser);

    /// <param name="PreviousPath">Where a moved file was.</param>
    /// <param name="LockedBy">Who else holds the file's lock, or null. A change locked by someone else can't be submitted.</param>
    /// <param name="ServerChanged">The file also changed on the server since the last sync. Syncing leaves it alone.</param>
    public sealed record ChangeDto(string Path, ChangeKind Kind, string? PreviousPath, long Size, LockHolderDto? LockedBy, bool ServerChanged);

    /// <param name="SyncedRevision">The revision the workspace last synced to.</param>
    /// <param name="NewRevisions">Revisions on the branch since then, up to 100 (shown as 99+).</param>
    /// <param name="LatestRevision">The branch's latest revision known, at least <paramref name="SyncedRevision"/>.</param>
    /// <param name="LockedByMe">Files this workspace holds locks on.</param>
    /// <param name="ServerProblem">Why the server couldn't be asked about locks and new revisions, or null. The changes are still right.</param>
    /// <param name="ScanMilliseconds">How long the scan took.</param>
    public sealed record StatusDto(WorkspaceDto Workspace, IReadOnlyList<ChangeDto> Changes, IReadOnlyList<string> Unreadable, long SyncedRevision, int NewRevisions,
        long LatestRevision, int LockedByMe, int LockedByOthers, string? ServerProblem, long ScanMilliseconds);

    /// <param name="Mine">Held by this workspace.</param>
    /// <param name="CreatedAt">Unix milliseconds.</param>
    public sealed record LockDto(string Path, string Username, string Host, bool Mine, long CreatedAt);

    public sealed record OpenWorkspaceRequest(string Path);

    public sealed record PickFolderRequest(string? Title, string? StartIn);

    /// <param name="Path">The folder chosen, or null when the picker was closed.</param>
    public sealed record PickFolderResponse(string? Path);

    public sealed record PathRequest(string Path);

    /// <param name="Folder">The folder to clone into; a folder named after the project is made inside it.</param>
    /// <param name="Branch">The branch to follow. Defaults to <c>main</c>.</param>
    public sealed record CloneRequest(Guid ProjectId, string Folder, string? Branch);

    /// <param name="Folder">The folder that becomes the workspace. Files already in it are kept.</param>
    /// <param name="Name">The project's name. Defaults to the folder's name.</param>
    /// <param name="Template">An ignore template (e.g. <c>unreal</c>), <c>none</c>, or null to detect one.</param>
    public sealed record InitRequest(string Folder, string? Name, string? Template);

    /// <param name="Released">Locks released.</param>
    /// <param name="Refused">Locks the server didn't release, because only people with manage access can release others'.</param>
    public sealed record UnlockResponse(IReadOnlyList<string> Released, IReadOnlyList<string> Refused);

    public sealed record UnlockRequest(IReadOnlyList<string> Paths);

    /// <param name="Name">For <c>.vxignore</c> templates, e.g. <c>unreal</c>.</param>
    public sealed record TemplateDto(string Name, string DisplayName);

    /// <summary>
    /// The open workspace: its changes, locks, and the folders the app knows.
    /// </summary>
    [ApiController]
    [Route("api/workspaces")]
    public sealed class WorkspacesController(WorkspaceRegistry registry, OperationRunner runner, DesktopShell shell, ServerSession session) : ControllerBase
    {
        /// <summary>Revisions counted as new before the count shows as 99+.</summary>
        private const int NewRevisionLimit = 100;

        /// <summary>
        /// The open workspace (reopening the last one on first call) and the workspaces opened before.
        /// </summary>
        [HttpGet]
        public async Task<WorkspacesDto> List(CancellationToken cancellationToken)
        {
            LinkedWorkspace? current = await registry.CurrentAsync(cancellationToken);
            List<RecentWorkspaceDto> recent = registry.Recent()
                .Select(r => new RecentWorkspaceDto(r.Root, FolderName(r.Root), r.Project, r.Branch, r.LastOpened, Directory.Exists(Path.Combine(r.Root, VWorkspace.MetadataDirectoryName))))
                .ToList();

            return new WorkspacesDto(current is null ? null : Describe(current), recent);
        }

        /// <summary>
        /// Opens a folder that is (or is inside) a workspace and makes it the current one.
        /// </summary>
        [HttpPost("open")]
        public async Task<WorkspaceDto> Open(OpenWorkspaceRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Path)) throw new HostProblemException("Pick a folder to open.");
            return Describe(await registry.OpenAsync(request.Path.Trim(), cancellationToken));
        }

        /// <summary>
        /// Removes a workspace from the recent list. Its files stay where they are.
        /// </summary>
        [HttpPost("forget")]
        public async Task<IActionResult> Forget(PathRequest request, CancellationToken cancellationToken)
        {
            await registry.ForgetAsync(Path.GetFullPath(request.Path), cancellationToken);
            return NoContent();
        }

        /// <summary>
        /// Shows the native folder picker.
        /// </summary>
        [HttpPost("pick-folder")]
        public PickFolderResponse PickFolder(PickFolderRequest request)
        {
            return new PickFolderResponse(shell.PickFolder(request.Title ?? "Choose a folder", request.StartIn));
        }

        /// <summary>
        /// The <c>.vxignore</c> templates a new project can start with.
        /// </summary>
        [HttpGet("templates")]
        public IReadOnlyList<TemplateDto> Templates()
        {
            return IgnoreTemplate.All.Select(t => new TemplateDto(t.Name, t.DisplayName)).ToList();
        }

        /// <summary>
        /// Clones a project into a new folder inside <see cref="CloneRequest.Folder"/>, like <c>vx clone</c>, and opens
        /// it. The download runs as an operation.
        /// </summary>
        [HttpPost("clone")]
        public async Task<OperationDto> Clone(CloneRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Folder)) throw new HostProblemException("Pick a folder to clone into.");
            string server = session.Server;
            string username = session.Credential?.Username ?? throw new Vertex.Core.Net.NotLoggedInException(server);

            using ProjectInterface projects = session.Connect((s, token) => new ProjectInterface(s, token));
            VProject project = Unwrap(await projects.GetProjectByIdAsync(request.ProjectId.ToString(), cancellationToken), "That project doesn't exist any more. Pick another one.");
            string branchName = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch.Trim();
            using BranchInterface branches = session.Connect((s, token) => new BranchInterface(s, token));
            VBranch branch = Unwrap(await branches.GetBranchByNameAsync(project.Id, branchName, cancellationToken), $"Project '{project.Name}' has no branch '{branchName}'. Pick another one.");

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(request.Folder.Trim(), project.Name)));
            VWorkspace? workspace = VWorkspace.Find(root);
            if (workspace is not null)
            {
                bool sameRoot = WorkspaceScanner.PathComparer.Equals(workspace.Root, root);
                if (!sameRoot || workspace.Config.ProjectId != project.Id)
                {
                    string linkedTo = workspace.Config.Project is null ? "" : $" of project '{workspace.Config.Project}'";
                    throw new HostProblemException($"'{workspace.Root}' is already a Vertex workspace{linkedTo}. Clone into another folder.", StatusCodes.Status409Conflict);
                }

                if (workspace.Config.BranchId != branch.Id)
                    throw new HostProblemException($"'{workspace.Root}' is already a workspace of {project.Name} on branch '{workspace.Config.Branch}'. Open it and switch branches there.",
                        StatusCodes.Status409Conflict);
            }
            else
            {
                VWorkspace.Initialize(root, new WorkspaceConfig
                {
                    Project = project.Name,
                    ProjectId = project.Id,
                    Branch = branch.Name,
                    BranchId = branch.Id,
                    User = username
                });
            }

            await registry.OpenAsync(root, cancellationToken);
            string title = $"Cloning {project.Name}";
            return runner.Start("clone", title, async (linked, reporter, ct) =>
            {
                SyncOutcome outcome = await WorkspaceSync.RunAsync(linked, registry.Connection(linked), 0, reporter, ct);
                return OperationResultDto.Success($"Cloned {project.Name}/{branch.Name} at #{outcome.Head.HeadRevision}: {outcome.Updated:N0} file(s) downloaded.",
                    outcome.Head.HeadRevision, Warnings(outcome.Locks), outcome.Conflicts);
            });
        }

        /// <summary>
        /// Creates a project on the server and makes the folder its workspace, like <c>vx init</c>, then opens it.
        /// </summary>
        [HttpPost("init")]
        public async Task<WorkspaceDto> Init(InitRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Folder)) throw new HostProblemException("Pick the folder for the new project.");
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Folder.Trim()));
            VWorkspace? existing = VWorkspace.Find(root);
            if (existing is not null) throw new HostProblemException($"'{existing.Root}' is already a Vertex workspace. Open it instead.", StatusCodes.Status409Conflict);

            IgnoreTemplate? template = null;
            if (request.Template is not null && !request.Template.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                template = IgnoreTemplate.Find(request.Template) ?? throw new HostProblemException($"Unknown template '{request.Template}'.");
            }
            else if (request.Template is null && Directory.Exists(root))
            {
                template = IgnoreTemplate.DetectFor(root);
            }

            string name = string.IsNullOrWhiteSpace(request.Name) ? new DirectoryInfo(root).Name : request.Name.Trim();
            string username = session.Credential?.Username ?? throw new Vertex.Core.Net.NotLoggedInException(session.Server);
            using ProjectInterface projects = session.Connect((s, token) => new ProjectInterface(s, token));
            ServerResult<CreateProjectResponse> result = await projects.CreateProjectAsync(name, "main", cancellationToken);
            if (result.StatusCode == 409) throw new HostProblemException($"Project '{name}' already exists. Clone it instead, or pick another name.", StatusCodes.Status409Conflict);
            CreateProjectResponse created = Unwrap(result, "Couldn't create the project.");

            Directory.CreateDirectory(root);
            VWorkspace.Initialize(root, new WorkspaceConfig
            {
                Project = created.Project.Name,
                ProjectId = created.Project.Id,
                Branch = created.DefaultBranch.Name,
                BranchId = created.DefaultBranch.Id,
                User = username
            });

            if (template is not null && !System.IO.File.Exists(Path.Combine(root, IgnoreRules.FileName))) template.WriteTo(root);
            return Describe(await registry.OpenAsync(root, cancellationToken));
        }

        /// <summary>
        /// Scans the open workspace like <c>vx status</c>: its changes, who else holds their locks, and how many
        /// revisions the branch has that this workspace hasn't synced. Works offline; the server parts are then left
        /// out and <see cref="StatusDto.ServerProblem"/> says why.
        /// </summary>
        [HttpGet("current/status")]
        public async Task<StatusDto> Status(CancellationToken cancellationToken)
        {
            if (runner.Current is not null && _lastStatus is not null) return _lastStatus;

            StatusDto status = await registry.ExclusiveAsync(async linked =>
            {
                Stopwatch timer = Stopwatch.StartNew();
                using WorkspaceIndex index = linked.Workspace.OpenIndex();
                WorkspaceStatus scan = await Task.Run(() => WorkspaceScanner.Scan(linked.Workspace, index, new ScanOptions { MaxConcurrency = Environment.ProcessorCount }, cancellationToken),
                    cancellationToken);
                timer.Stop();

                long.TryParse(index.GetMeta(WorkspaceIndex.SyncedRevisionKey), out long synced);
                Dictionary<string, HeldLock> held = new(StringComparer.Ordinal);
                HashSet<string> serverChanged = new(WorkspaceScanner.PathComparer);
                int newRevisions = 0, lockedByMe = 0, lockedByOthers = 0;
                long latest = synced;
                string? problem = null;
                try
                {
                    WorkspaceConnection connection = registry.Connection(linked);
                    using LockInterface lockClient = new(connection.ServerAddress, connection.Token);
                    LockReport locks = await connection.OpenLocks(linked, index, lockClient).ReconcileAsync(scan.Changes, ct: cancellationToken);
                    if (locks.Error is not null) throw locks.Error;
                    foreach (HeldLock holder in locks.HeldByOthers) held[RepositoryPath.ToKey(holder.Path)] = holder;
                    lockedByMe = locks.LockedByMe;
                    lockedByOthers = locks.LockedByOthers;

                    using RevisionInterface revisions = new(connection.ServerAddress, connection.Token);
                    IReadOnlyList<RevisionSummary> since = await revisions.GetRevisionsAsync(linked.ProjectId, linked.BranchId, synced, NewRevisionLimit, cancellationToken);
                    newRevisions = since.Count;
                    if (since.Count > 0) latest = since[^1].Number;

                    // Local changes to files that also changed on the server: a sync leaves them alone, so flag them.
                    if (since.Count > 0 && synced > 0 && scan.Changes.Count > 0)
                    {
                        HeadChanges head = await revisions.GetHeadChangesAsync(linked.ProjectId, linked.BranchId, synced, cancellationToken);
                        if (!head.Full)
                        {
                            foreach (HeadChange file in head.Files) serverChanged.Add(file.Path);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    problem = ServerErrorHandler.Describe(ex, session).Detail;
                }

                List<ChangeDto> changes = scan.Changes.Select(c => new ChangeDto(c.Path, Kind(c.Type), c.PreviousPath, c.Size, Holder(held, c),
                    serverChanged.Contains(c.Path) || (c.PreviousPath is not null && serverChanged.Contains(c.PreviousPath)))).ToList();

                return new StatusDto(Describe(linked), changes, scan.Unreadable.ToList(), synced, newRevisions, latest, lockedByMe, lockedByOthers, problem, timer.ElapsedMilliseconds);
            }, cancellationToken);

            _lastStatus = status;
            return status;
        }

        /// <summary>
        /// Every lock on the open workspace's branch, this workspace's first, as the server has them now.
        /// </summary>
        [HttpGet("current/locks")]
        public async Task<IReadOnlyList<LockDto>> Locks(CancellationToken cancellationToken)
        {
            return await registry.ExclusiveAsync(async linked =>
            {
                WorkspaceConnection connection = registry.Connection(linked);
                using WorkspaceIndex index = linked.Workspace.OpenIndex();
                using LockInterface client = new(connection.ServerAddress, connection.Token);
                WorkspaceLocks locks = connection.OpenLocks(linked, index, client);
                LockReport report = await locks.RefreshAsync(cancellationToken);
                if (report.Error is not null) throw report.Error;

                return (IReadOnlyList<LockDto>)locks.Current()
                    .OrderByDescending(l => l.Mine)
                    .ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(l => new LockDto(l.Path, l.Username, l.Host, l.Mine, l.CreatedAt))
                    .ToList();
            }, cancellationToken);
        }

        /// <summary>
        /// Releases locks held somewhere else, like <c>vx unlock</c>: your own from a workspace that's gone, or anyone's
        /// with manage access. This workspace's own locks are released by submitting or reverting the files.
        /// </summary>
        [HttpPost("current/unlock")]
        public async Task<UnlockResponse> Unlock(UnlockRequest request, CancellationToken cancellationToken)
        {
            PathFilter filter = PathFilter.FromRelative(request.Paths);
            UnlockResponse response = await registry.ExclusiveAsync(async linked =>
            {
                WorkspaceConnection connection = registry.Connection(linked);
                using WorkspaceIndex index = linked.Workspace.OpenIndex();
                using LockInterface client = new(connection.ServerAddress, connection.Token);
                WorkspaceLocks locks = connection.OpenLocks(linked, index, client);
                LockReport report = await locks.RefreshAsync(cancellationToken);
                if (report.Error is not null) throw report.Error;

                List<string> elsewhere = locks.Current().Where(l => !l.Mine && filter.Matches(l.Path)).Select(l => l.Path).ToList();
                if (elsewhere.Count == 0) return new UnlockResponse([], []);

                HashSet<string> released = (await client.UnlockAsync(linked.ProjectId, linked.BranchId, linked.WorkspaceId, elsewhere, force: true, cancellationToken))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                await locks.RefreshAsync(cancellationToken);
                return new UnlockResponse(elsewhere.Where(released.Contains).ToList(), elsewhere.Where(p => !released.Contains(p)).ToList());
            }, cancellationToken);

            registry.PublishChanged();
            return response;
        }

        /// <summary>
        /// Shows a file or folder of the open workspace in the system file manager.
        /// </summary>
        [HttpPost("current/reveal")]
        public async Task<IActionResult> Reveal(PathRequest request, CancellationToken cancellationToken)
        {
            LinkedWorkspace linked = await registry.RequiredAsync(cancellationToken);
            string full = string.IsNullOrWhiteSpace(request.Path) ? linked.Workspace.Root : linked.Workspace.ToFullPath(request.Path);
            string root = Path.TrimEndingDirectorySeparator(linked.Workspace.Root);
            if (!WorkspaceScanner.PathComparer.Equals(full, root) && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new HostProblemException("That path is outside the workspace.");

            shell.Reveal(full);
            return NoContent();
        }

        /// <summary>The last status scanned, returned while an operation holds the workspace.</summary>
        private static StatusDto? _lastStatus;

        internal static IReadOnlyList<string> Warnings(LockReport? locks)
        {
            if (locks?.Error is null) return [];
            return [$"Couldn't update file locks: {locks.Error.Message}"];
        }

        private WorkspaceDto Describe(LinkedWorkspace linked)
        {
            WorkspaceConfig config = linked.Workspace.Config;
            return new WorkspaceDto(linked.Workspace.Root, FolderName(linked.Workspace.Root), config.Project ?? FolderName(linked.Workspace.Root), linked.ProjectId,
                config.Branch ?? "main", linked.BranchId, registry.ServerFor(linked));
        }

        private static string FolderName(string root)
        {
            return new DirectoryInfo(root).Name;
        }

        private static ChangeKind Kind(ChangeType type)
        {
            return type switch
            {
                ChangeType.Created => ChangeKind.Added,
                ChangeType.Modified => ChangeKind.Modified,
                ChangeType.Deleted => ChangeKind.Deleted,
                _ => ChangeKind.Moved
            };
        }

        private static LockHolderDto? Holder(Dictionary<string, HeldLock> held, WorkspaceChange change)
        {
            if (!held.TryGetValue(RepositoryPath.ToKey(change.Path), out HeldLock? holder) &&
                (change.PreviousPath is null || !held.TryGetValue(RepositoryPath.ToKey(change.PreviousPath), out holder))) return null;
            return new LockHolderDto(holder.Username, holder.Host, holder.SameUser);
        }

        private static T Unwrap<T>(ServerResult<T> result, string notFound)
        {
            if (result.IsSuccessStatusCode && result.Data is not null) return result.Data;
            if (result.Exception is not null && result.StatusCode == 0) throw result.Exception;
            if (result.StatusCode == 404) throw new HostProblemException(notFound, StatusCodes.Status404NotFound);
            string reason = string.IsNullOrWhiteSpace(result.Exception?.Message) ? $"The server returned {result.StatusCode}." : result.Exception.Message;
            throw new HostProblemException(reason, result.StatusCode is >= 400 and < 500 ? result.StatusCode : StatusCodes.Status502BadGateway);
        }
    }
}
