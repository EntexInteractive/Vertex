// Copyright 2026 Entex Interactive

namespace Vertex.Server
{
    public class ServerSettings
    {
        /// <summary>
        ///     Main port for serving HTTP.
        /// </summary>
        public int HttpPort { get; set; } = 5000;

        /// <summary>
        ///     Dedicated port for serving only HTTP/2.
        /// </summary>
        public int Http2Port { get; set; } = 5002;
    }
}