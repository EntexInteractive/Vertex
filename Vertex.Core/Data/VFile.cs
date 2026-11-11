// Copyright 2026 Entex Interactive

using EntexInteractive.Extensions;
using Vertex.Core.Storage;

namespace Vertex.Core.Data
{
    public class VFile
    {
        /// <summary>
        /// SHA-256 hash of the file's content.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// The name of the file.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The universal path of the file.
        /// </summary>
        public string Fullname { get; set; }

        /// <summary>
        /// The time the file was last written to.
        /// </summary>
        public UnixTime LastWrite { get; set; }

        /// <summary>
        /// The branch id the file belongs to.
        /// </summary>
        public Guid Branch { get; set; }

        /// <summary>
        /// The size, in bytes, of the local file.
        /// </summary>
        public long PhysicalSize { get; set; }

        /// <summary>
        /// The size, in bytes, of the compressed file.
        /// </summary>
        public long CompressedSize { get; set; }

        /// <summary>
        /// The user that currently has the file locked. Null if unlocked.
        /// </summary>
        public Guid? LockedBy { get; set; }

        /// <summary>
        /// Content-addressed file id (see <see cref="ContentId"/>). Identical content always has the same id.
        /// </summary>
        public string? CheckSum { get; set; }

        /// <summary>
        /// The file's chunk layout, populated by <see cref="TryGenerateCheckSum"/>. Local-only; not serialized.
        /// </summary>
        [JsonIgnore]
        public ChunkedFile? Chunks { get; private set; }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="LocalFile"/> class with default properties.
        /// </summary>
        public VFile(string path, Guid branch)
        {
            Name = Path.GetFileName(path);
            Fullname = Path.GetFullPath(path);
            if (!File.Exists(path)) return;
            
            FileInfo fi = new(path);
            PhysicalSize = fi.Length;
            LastWrite = new UnixTime(fi.LastWriteTimeUtc);
            Branch = branch;
        }

        /// <summary>
        /// Attempts to generate and assign a checksum for the current file based on its contents.
        /// </summary>
        /// <return>
        /// <see langword="true"/> if a checksum already exists or is generated successfully; otherwise, <see langword="false"/> if the file does not exist or an error occurs during generation.
        /// </return>
        public bool TryGenerateCheckSum()
        {
            if (!string.IsNullOrEmpty(CheckSum)) return true;

            try
            {
                if (!File.Exists(Fullname)) return false;

                // The checksum is the content-addressed file id: a hash over the file's content-defined chunks.
                Chunks = ContentChunker.ChunkFile(Fullname);
                PhysicalSize = Chunks.Size;
                CheckSum = Chunks.FileId;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while generating checksum");
                return false;
            }
        }
    }
}