// Copyright 2026 Entex Interactive

namespace Vertex.Core.Data
{
    public enum ChangeType
    {
        Created,
        Changed,
        Deleted
    }

    public class VChange
    {
        public VFile File { get; }
        public ChangeType Type { get; }
    }
}