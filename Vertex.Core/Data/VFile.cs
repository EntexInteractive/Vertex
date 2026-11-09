// Copyright 2026 Entex Interactive

using System.Security.Cryptography;
using EntexInteractive.Extensions;
using Vertex.Core.IO;

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
        /// The branch of the file.
        /// </summary>
        public Guid BranchId { get; set; }

        /// <summary>
        /// The size, in bytes, of the file.
        /// </summary>
        public long PhysicalSize { get; set; }

        /// <summary>
        /// The user that currently has the file locked. Null if unlocked.
        /// </summary>
        public Guid? LockedBy { get; set; }
        public string? CheckSum { get; set; }
        public long Length { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalFile"/> class with default properties.
        /// </summary>
        public VFile(string path, string branchId)
        {
            Name = Path.GetFileName(path);
            Fullname = Path.GetFullPath(path);
            if (!File.Exists(path)) return;
            
            FileInfo fi = new(path);
            PhysicalSize = fi.Length;
            LastWrite = new UnixTime(fi.LastWriteTimeUtc);
            BranchId = Guid.Parse(branchId);
        }
        
        public bool TryGenerateCheckSum()
        {
            if (!string.IsNullOrEmpty(CheckSum)) return true;

            try
            {
                if (!File.Exists(Fullname)) return false;

                using SHA256 sha256 = SHA256.Create();
                using FileStream fs = File.OpenRead(Fullname);
                
                fs.Position = 0;
                CheckSum = Convert.ToHexStringLower(sha256.ComputeHash(fs));
                return !string.IsNullOrEmpty(CheckSum);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while generating checksum");
                return false;
            }
        }
    }
}