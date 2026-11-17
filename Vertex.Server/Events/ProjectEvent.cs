// Copyright 2026 Entex Interactive

using System.Text.Json;

namespace Vertex.Server.Events
{
    /// <summary>
    /// The kinds of project events. Names are <c>area.what</c>; add new ones here.
    /// </summary>
    public static class EventTypes
    {
        /// <summary>Paths were locked on a branch (<see cref="LockPayload"/>).</summary>
        public const string LockLocked = "lock.locked";

        /// <summary>Locks on a branch were released (<see cref="LockPayload"/>, with a reason).</summary>
        public const string LockReleased = "lock.released";

        /// <summary>A revision was submitted to a branch (<see cref="RevisionPayload"/>).</summary>
        public const string RevisionCreated = "revision.created";
    }

    /// <summary>
    /// Why locks were released, in <see cref="LockPayload.Reason"/>.
    /// </summary>
    public static class LockReleaseReasons
    {
        public const string Submitted = "submitted";
        public const string Reverted = "reverted";
        public const string Unlocked = "unlocked";
    }

    public sealed record LockPayload(string[] Paths, string? Reason = null);

    public sealed record RevisionPayload(long Revision, string Message, int Files);

    /// <summary>
    /// An event to record, before it has a seq.
    /// </summary>
    public sealed record NewEvent(string Type, Guid? BranchId, Guid UserId, Guid? WorkspaceId, string? Host, object Payload);

    /// <summary>
    /// A recorded project event. <see cref="Payload"/> is JSON; read it with <see cref="Read{T}"/>.
    /// </summary>
    public sealed record ProjectEvent(Guid ProjectId, long Seq, string Type, Guid? BranchId, Guid UserId, string Username, Guid? WorkspaceId, string? Host,
        string Payload, long CreatedAt)
    {
        internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public T Read<T>()
        {
            return JsonSerializer.Deserialize<T>(Payload, Json) ?? throw new InvalidDataException($"Event {Seq} has an empty payload.");
        }
    }
}
