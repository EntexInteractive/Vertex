# Vertex.Core

This library owns shared models, file I/O, and file transfer client logic. Changes to shared types can affect multiple executables. The file-transfer gRPC types are generated from ../Vertex.Server/Protos/Files.proto. Edit the schema at its source and build Vertex.sln for shared contract changes.

From the Vertex repository root, run `dotnet build Vertex.Core/Vertex.Core.csproj`.
