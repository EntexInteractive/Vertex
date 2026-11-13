# Vertex.Agent

Background device agent (assembly `VertexAgent`). It will keep a device connected to the server and synchronize its
files. It's early: today it connects over gRPC and runs placeholder services. See the root AGENTS.md for project-wide
rules.

- `AgentApp.cs`: host setup and dependency injection.
- `AgentSettings.cs`: configuration (`ServerAddress`), bound from `appsettings.json`.
- `Services/GrpcService.cs` handles the server connection; `Services/FileService.cs` handles file synchronization.

The agent is meant to run as a Windows Service and as a systemd unit (the hosting packages are already referenced),
so keep it cross-platform. Reconnects and retries must survive the server or network going away; exercise those paths
when changing synchronization.

Build: `dotnet build Vertex.Agent/Vertex.Agent.csproj` from the repository root.
