// Copyright 2026 Entex Interactive

namespace Vertex.Core.Settings
{
    public class ClientSettings
    {
        /// <summary>
        /// Gets the path to the application's roaming data directory.
        /// </summary>
        /// <remarks>
        /// This directory is located under the current user's roaming application data folder
        /// and is created automatically if it does not already exist.
        /// </remarks>
        public static string RoamingDirectory
        {
            get
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Entex Interactive", "Vertex");
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                return path;
            }
        }
    }
}