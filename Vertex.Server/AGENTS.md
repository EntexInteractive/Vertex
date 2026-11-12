# Vertex.Server

The ASP.NET Core server hosts REST and gRPC endpoints. Start in Startup.cs for service wiring, Protos/ for RPC
contracts, Database/ for persistence and caching, and Storage/ for the S3 chunk store. Server configuration comes only
from environment variables, read and validated in ServerSettings.cs (see the README table); add new settings there
rather than reading the environment elsewhere. Build Vertex.sln when changing shared protobuf contracts.

From the Vertex repository root, run `dotnet build Vertex.Server/Vertex.Server.csproj`.
