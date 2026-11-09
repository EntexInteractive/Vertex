# Vertex.Agent

The device agent hosts file synchronization. Start in AgentApp.cs for host wiring, AgentSettings.cs for configuration, and Services/ for file and gRPC behavior. Preserve both Windows Service and systemd hosting. Exercise reconnect and retry behavior when changing synchronization.

From the Vertex repository root, run `dotnet build Vertex.Agent/Vertex.Agent.csproj`.
