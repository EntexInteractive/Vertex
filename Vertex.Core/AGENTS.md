# Vertex.Core

Shared library referenced by every executable, so a change here can affect the server, CLI, agent and GUI at once.
See the root AGENTS.md for project-wide rules.

- `Data/`: shared models (`VProject`, `VBranch`, `VFile`, ...). They are serialized over REST and stored by Dapper,
  so renaming a property is a contract change.
- `Net/Interfaces/`: typed clients for the server. `BaseInterface` owns the `HttpClient` and the gRPC channel, which
  always uses gRPC-Web (`GrpcWebHandler`). Don't switch to native gRPC: Cloudflare Tunnel strips HTTP/2 trailers.
  `FilesInterface` batches uploads and downloads.
- `Net/FileTransferLimits.cs`: message, batch and window sizes shared by client and server. Change them only here so
  both sides stay in sync.
- `Storage/`: `ContentChunker` (FastCDC content-defined chunking, at most 4 MB per chunk), `ChunkCodec` (zstd) and
  `ContentId` (content hashes). Chunk boundaries and ids decide deduplication and what is stored, so changing the
  algorithm or its parameters breaks existing content. Treat that as a storage format change.
- The gRPC code is generated from `../Vertex.Server/Protos/Files.proto`, for both client and server. Edit the proto
  there, then build `Vertex.sln`.

Performance matters most here, because chunking and hashing run on every file the client touches. Avoid extra copies
and allocations on hot paths, and measure with large files.

Build: `dotnet build Vertex.Core/Vertex.Core.csproj` from the repository root, then `dotnet build Vertex.sln` for
contract changes.
