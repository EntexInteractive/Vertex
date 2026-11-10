// Copyright 2026 Entex Interactive

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vertex.Server
{
    public static class ServerSettingsManager
    {
        private static readonly string ConfigPath = Environment.GetEnvironmentVariable("VERTEX_SERVER_CONFIG") ?? Path.Combine(AppContext.BaseDirectory, "VertexServer.yml");
        private static readonly ISerializer Serializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        private static readonly IDeserializer Deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();

        /// <summary>
        /// Loads server settings from the configuration file.
        /// If the configuration file does not exist, creates a new settings instance,
        /// saves it to the default configuration path, and returns it.
        /// </summary>
        /// <return>
        /// A <see cref="ServerSettings"/> instance loaded from the configuration file,
        /// or a newly created default instance if the file does not exist.
        /// </return>
        /// <exception cref="InvalidDataException">
        /// Thrown when the configuration file exists but is empty or contains invalid data.
        /// </exception>
        public static ServerSettings Load()
        {
            if (!File.Exists(ConfigPath))
            {
                ServerSettings defaults = new();
                Save(defaults);
                return defaults;
            }

            string yaml = File.ReadAllText(ConfigPath);
            return Deserializer.Deserialize<ServerSettings>(yaml) ?? throw new InvalidDataException($"Configuration file '{ConfigPath}' is empty or invalid.");
        }

        /// <summary>
        /// Saves the specified server settings to the default configuration file.
        /// Serializes the settings instance to YAML and overwrites any existing file at the configured path.
        /// </summary>
        /// <param name="settings">
        /// The <see cref="ServerSettings"/> instance to serialize and write to the configuration file.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="settings"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when an I/O error occurs while writing the configuration file.
        /// </exception>
        public static void Save(ServerSettings settings)
        {
            File.WriteAllText(ConfigPath, Serializer.Serialize(settings));
        }
    }

    public class ServerSettings
    {
        public StorageSettings Storage { get; set; } = new();
    }

    public class StorageSettings
    {
        public string Endpoint { get; set; } = "https://s3.amazonaws.com";
        public string Region { get; set; } = "us-east-1";
        public string Bucket { get; set; } = "vertex-storage";
        public string AccessKey { get; set; } = string.Empty;
        public string SecretKey { get; set; } = string.Empty;
    }
}
