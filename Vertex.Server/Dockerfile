FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# The SDK stage runs on the build machine's own architecture and cross-compiles for the target (amd64 or arm64),
# so multi-platform builds don't run the compiler under emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
WORKDIR /src
COPY ["Vertex.Server/Vertex.Server.csproj", "Vertex.Server/"]
COPY ["Vertex.Core/Vertex.Core.csproj", "Vertex.Core/"]
RUN dotnet restore "Vertex.Server/Vertex.Server.csproj" -a $TARGETARCH
COPY . .
WORKDIR "/src/Vertex.Server"
RUN dotnet publish "./Vertex.Server.csproj" -c $BUILD_CONFIGURATION -a $TARGETARCH -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Vertex.Server.dll"]
