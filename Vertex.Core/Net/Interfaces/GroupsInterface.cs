// Copyright 2026 Entex Interactive

using Vertex.Core.Data;
using Vertex.Core.Net.Results;

namespace Vertex.Core.Net.Interfaces
{
    public sealed class GroupsInterface(string url, string? token = null) : BaseInterface(url, token)
    {
        public Task<ServerResult<IReadOnlyList<VGroup>>> ListGroupsAsync(CancellationToken ct = default)
        {
            return SendAsync<IReadOnlyList<VGroup>>(HttpMethod.Get, "api/v1/groups", cancellationToken: ct);
        }

        public Task<ServerResult<VGroup>> CreateGroupAsync(string name, CancellationToken ct = default)
        {
            return SendAsync<VGroup>(HttpMethod.Post, $"api/v1/groups?name={Uri.EscapeDataString(name)}", cancellationToken: ct);
        }

        public Task<ServerResult<bool>> DeleteGroupAsync(string name, CancellationToken ct = default)
        {
            return SendAsync<bool>(HttpMethod.Delete, $"api/v1/groups?name={Uri.EscapeDataString(name)}", cancellationToken: ct);
        }

        public Task<ServerResult<IReadOnlyList<VUser>>> ListMembersAsync(string name, CancellationToken ct = default)
        {
            return SendAsync<IReadOnlyList<VUser>>(HttpMethod.Get, $"api/v1/groups/members?name={Uri.EscapeDataString(name)}", cancellationToken: ct);
        }

        public Task<ServerResult<int>> AddMembersAsync(string name, IEnumerable<string> usernames, CancellationToken ct = default)
        {
            return SendAsync<int>(HttpMethod.Post, MembersQuery(name, usernames), cancellationToken: ct);
        }

        public Task<ServerResult<int>> RemoveMembersAsync(string name, IEnumerable<string> usernames, CancellationToken ct = default)
        {
            return SendAsync<int>(HttpMethod.Delete, MembersQuery(name, usernames), cancellationToken: ct);
        }

        private static string MembersQuery(string name, IEnumerable<string> usernames)
        {
            return $"api/v1/groups/members?name={Uri.EscapeDataString(name)}" + string.Concat(usernames.Select(u => "&users=" + Uri.EscapeDataString(u)));
        }
    }
}
