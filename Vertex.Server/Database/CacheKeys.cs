// Copyright 2026 Entex Interactive

namespace Vertex.Server.Database
{
    /// <summary>
    /// Every cache key the server uses, so the code that reads a value and the code that invalidates it can't drift
    /// apart. The text before the first ':' is the category on <c>vertex.cache.lookups</c>.
    /// </summary>
    /// <remarks>
    /// Keys are case-sensitive. Names the database compares ignoring case (users, groups) are lowercased here; names
    /// it compares exactly (projects, branches) are kept as given. Generation keys start with <c>gen:</c>; see
    /// <see cref="CacheContext.GetGenerationAsync"/>.
    /// </remarks>
    public static class CacheKeys
    {
        // Generations, for values that too many writes affect to delete one by one.

        /// <summary>Access levels everywhere: group membership, admin flags, deleted groups.</summary>
        public const string AccessGeneration = "gen:access";

        /// <summary>Which projects each user can see: projects created, grants, membership.</summary>
        public const string ProjectListGeneration = "gen:projects";

        /// <summary>The user list.</summary>
        public const string UserListGeneration = "gen:users";

        /// <summary>Groups and their members.</summary>
        public const string GroupGeneration = "gen:groups";

        /// <summary>Access levels and grants in one project.</summary>
        public static string ProjectAccessGeneration(Guid projectId)
        {
            return $"gen:access:{projectId:N}";
        }

        // Projects

        public static string ProjectById(Guid projectId)
        {
            return $"project:id:{projectId:N}";
        }

        public static string ProjectByName(string name)
        {
            return $"project:name:{name}";
        }

        public static string ProjectList(string generation, Guid userId)
        {
            return $"project:list:{generation}:{userId:N}";
        }

        public static string Access(string globalGeneration, string projectGeneration, Guid projectId, Guid userId)
        {
            return $"access:{globalGeneration}:{projectGeneration}:{projectId:N}:{userId:N}";
        }

        public static string Grants(string globalGeneration, string projectGeneration, Guid projectId)
        {
            return $"access:grants:{globalGeneration}:{projectGeneration}:{projectId:N}";
        }

        // Branches

        public static string BranchById(Guid projectId, Guid branchId)
        {
            return $"branch:id:{projectId:N}:{branchId:N}";
        }

        public static string BranchByName(Guid projectId, string name)
        {
            return $"branch:name:{projectId:N}:{name}";
        }

        public static string BranchList(Guid projectId)
        {
            return $"branch:list:{projectId:N}";
        }

        public static string BranchExists(Guid projectId, Guid branchId)
        {
            return $"branch:exists:{projectId:N}:{branchId:N}";
        }

        /// <summary>The revision a branch was copied from its parent at. It never changes, so it's cached long.</summary>
        public static string BranchBase(Guid branchId)
        {
            return $"branch:base:{branchId:N}";
        }

        /// <summary>A branch's latest revision number, kept in Redis with <see cref="CacheContext.SetMaxAsync"/>.</summary>
        public static string HeadRevision(Guid branchId)
        {
            return $"revision:head:{branchId:N}";
        }

        // Users and groups

        public static string UserById(Guid userId)
        {
            return $"user:id:{userId:N}";
        }

        public static string UserByName(string username)
        {
            return $"user:name:{username.ToLowerInvariant()}";
        }

        public static string UserList(string generation)
        {
            return $"user:list:{generation}";
        }

        public static string GroupByName(string generation, string name)
        {
            return $"group:name:{generation}:{name.ToLowerInvariant()}";
        }

        public static string GroupList(string generation)
        {
            return $"group:list:{generation}";
        }

        public static string GroupMembers(string generation, Guid groupId)
        {
            return $"group:members:{generation}:{groupId:N}";
        }

        // Content

        /// <summary>A file manifest. Content-addressed, so it never changes and is never invalidated.</summary>
        public static string Manifest(string fileId)
        {
            return $"manifest:{fileId}";
        }
    }
}
