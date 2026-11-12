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
        public ChangeType Type { get; }
        public VFile File { get; }

        public VChange(ChangeType type, VFile file)
        {
            Type = type;
            File = file;
        }
    }
}