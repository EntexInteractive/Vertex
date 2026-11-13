FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 5000

# The SDK stage runs on the build machine's own architecture and cross-compiles for the target (amd64 or arm64),
# so multi-platform builds don't run the compiler under emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
# Set by the Release workflow (e.g. VERSION=1.3.0-beta.1, ASSEMBLY_VERSION=1.3.0.0). Empty keeps the .csproj version.
ARG VERSION=
ARG ASSEMBLY_VERSION=
WORKDIR /src
COPY ["Vertex.Server/Vertex.Server.csproj", "Vertex.Server/"]
COPY ["Vertex.Core/Vertex.Core.csproj", "Vertex.Core/"]
RUN dotnet restore "Vertex.Server/Vertex.Server.csproj" -a $TARGETARCH
COPY . .
WORKDIR "/src/Vertex.Server"
RUN dotnet publish "./Vertex.Server.csproj" -c $BUILD_CONFIGURATION -a $TARGETARCH -o /app/publish /p:UseAppHost=false \
    ${VERSION:+-p:Version=$VERSION} \
    ${ASSEMBLY_VERSION:+-p:AssemblyVersion=$ASSEMBLY_VERSION -p:FileVersion=$ASSEMBLY_VERSION}

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY LICENSE /app/LICENSE
ENTRYPOINT ["dotnet", "VertexServer.dll"]
