// Copyright 2026 Entex Interactive

using System.Net;
using Grpc.Core;
using Vertex.Core.Client.Auth;
using Vertex.Core.Client.Config;
using Vertex.Core.Data;
using Vertex.Core.Net;
using Vertex.Core.Net.Interfaces;
using Vertex.Core.Net.Responses;
using Vertex.Core.Net.Results;

namespace Vertex.Mirror
{
    /// <summary>
    /// A setup problem the user can fix; the message says how. Stops the run.
    /// </summary>
    public sealed class MirrorSetupException(string message) : Exception(message);

    /// <summary>
    /// The Vertex side of a mirror: the server, the login, the project and branch, and the clients to talk to them.
    /// </summary>
    public sealed class VertexConnection : IDisposable
    {
        public const string DefaultServer = "http://127.0.0.1:5000";

        private VertexConnection(string server, string token)
        {
            Server = server;
            Token = token;
            Files = new FilesInterface(server, token) { MaxConcurrency = 4 };
            Revisions = new RevisionInterface(server, token);
        }

        public string Server { get; }

        public string Token { get; }

        public string ProjectName { get; private set; } = string.Empty;

        public Guid ProjectId { get; private set; }

        public string BranchName { get; private set; } = string.Empty;

        public Guid BranchId { get; private set; }

        public string Username { get; private set; } = string.Empty;

        public FilesInterface Files { get; }

        public RevisionInterface Revisions { get; }

        /// <summary>
        /// "project/branch", for messages.
        /// </summary>
        public string Describe => $"{ProjectName}/{BranchName}";

        /// <summary>
        /// Resolves the server and login and finds the project and branch, creating them when
        /// <paramref name="create"/> is set (pull writes into them; push only reads).
        /// </summary>
        public static async Task<VertexConnection> ConnectAsync(MirrorSettings settings, bool create, CancellationToken ct)
        {
            string server = (settings.Server ?? UserConfig.Load().Server ?? DefaultServer).TrimEnd('/');
            string? token = settings.Token ?? Environment.GetEnvironmentVariable("VX_MIRROR_TOKEN") ?? CredentialStore.Get(server)?.Token;
            if (string.IsNullOrEmpty(token))
            {
                // A typo in the address should say so, not suggest logging in to a server that isn't there.
                using ServerInterface probe = new(server);
                ServerResult<InfoResponse> reachable = await probe.GetInfoAsync(ct);
                if (!reachable.IsSuccessStatusCode) throw new MirrorSetupException($"Can't reach {server}: {reachable.Exception?.Message}. Check the address, or pass --server.");
                throw new MirrorSetupException($"Not logged in to {server}. Run 'vx login {server}' first, or pass --token.");
            }

            VertexConnection connection = new(server, token);
            try
            {
                await connection.FindAsync(settings.Project!, settings.Branch, create, ct);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private async Task FindAsync(string projectName, string branchName, bool create, CancellationToken ct)
        {
            using ServerInterface serverApi = new(Server, Token);
            ServerResult<InfoResponse> info = await serverApi.GetInfoAsync(ct);
            if (!info.IsSuccessStatusCode) throw new MirrorSetupException($"Can't reach {Server}: {info.Exception?.Message}. Check the address, or pass --server.");

            using AuthInterface auth = new(Server, Token);
            try
            {
                Username = Unwrap(await auth.GetCurrentUserAsync(ct)).Username;
            }
            catch (NotLoggedInException)
            {
                throw new MirrorSetupException($"The login for {Server} was revoked or has expired. Run 'vx login {Server}' again.");
            }

            using ProjectInterface projects = new(Server, Token);
            using BranchInterface branches = new(Server, Token);
            ServerResult<VProject> existing = await projects.GetProjectByNameAsync(projectName, ct);
            if (existing.IsSuccessStatusCode && existing.Data is not null)
            {
                ProjectId = existing.Data.Id;
                ProjectName = existing.Data.Name;
                ServerResult<VBranch> branch = await branches.GetBranchByNameAsync(ProjectId, branchName, ct);
                if (branch.IsSuccessStatusCode && branch.Data is not null)
                {
                    BranchId = branch.Data.Id;
                }
                else if (create)
                {
                    BranchId = Unwrap(await branches.CreateBranchAsync(ProjectId, branchName, cancellationToken: ct)).Id;
                    AnsiConsole.MarkupLine($"[grey]Created branch {Markup.Escape(branchName)} in {Markup.Escape(ProjectName)}.[/]");
                }
                else
                {
                    throw new MirrorSetupException($"Project {ProjectName} has no branch '{branchName}'. Pass the branch to mirror with --branch.");
                }
            }
            else if (create)
            {
                CreateProjectResponse created = Unwrap(await projects.CreateProjectAsync(projectName, branchName, ct));
                ProjectId = created.Project.Id;
                ProjectName = created.Project.Name;
                BranchId = created.DefaultBranch.Id;
                AnsiConsole.MarkupLine($"[grey]Created project {Markup.Escape(ProjectName)} with branch {Markup.Escape(branchName)}.[/]");
            }
            else
            {
                throw new MirrorSetupException($"There's no project '{projectName}' on {Server} that {Username} can read. Check the --project name.");
            }

            BranchName = branchName;
        }

        /// <summary>
        /// The data of a successful REST result, or an <see cref="HttpRequestException"/> carrying its status.
        /// </summary>
        private static T Unwrap<T>(ServerResult<T> result)
        {
            if (result.IsSuccessStatusCode && result.Data is not null) return result.Data;
            throw result.Exception as HttpRequestException
                  ?? new HttpRequestException(result.Exception?.Message ?? "Request failed.", result.Exception, result.StatusCode == 0 ? null : (HttpStatusCode)result.StatusCode);
        }

        /// <summary>
        /// A short description of a failed call, e.g. "grpc Unavailable: ..." or "http 429".
        /// </summary>
        public static string DescribeError(Exception exception)
        {
            return exception switch
            {
                RpcException rpc => $"grpc {rpc.StatusCode}: {rpc.Status.Detail}",
                HttpRequestException { StatusCode: HttpStatusCode code } http => $"http {(int)code}: {http.Message}",
                AggregateException { InnerExceptions.Count: > 0 } aggregate => DescribeError(aggregate.InnerExceptions[0]),
                _ => exception.Message
            };
        }

        public void Dispose()
        {
            Files.Dispose();
            Revisions.Dispose();
        }
    }
}
