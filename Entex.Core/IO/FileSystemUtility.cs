// Copyright 2026 Entex Interactive

using System.Reflection;

namespace Entex.Core.IO
{
    /// <summary>
    /// 
    /// </summary>
    public class FileSystemUtility
    {
        public static DirectoryInfo AppDirectory => new DirectoryInfo(AppContext.BaseDirectory);
    }
}