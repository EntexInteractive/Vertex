# Vertex.Server

The ASP.NET Core server hosts REST and gRPC endpoints. Start in Startup.cs for service wiring, Protos/ for RPC contracts, Database/ for persistence and caching, and Storage/ for S3 uploads. Keep S3 settings in server.yml and other server settings in appsettings.json. Build Vertex.sln when changing shared protobuf contracts.

From the Vertex repository root, run `dotnet build Vertex.Server/Vertex.Server.csproj`.
