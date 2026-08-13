// Copyright 2026 Entex Interactive

namespace Vertex.Server.Server
{
    public class GetPortsResponse
    {
        /// <summary>
        /// Port for HTTP communication
        /// </summary>
        public int? HttpPort { get; set; }

        /// <summary>
        /// Port number for gRPC communication
        /// </summary>
        public int? GrpcPort { get; set; }
    }

    public class GetVersionResponse
    {
        public int Major { get; set; }
        public int Minor { get; set; }
        public int Build { get; set; }
    }
}