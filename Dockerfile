FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Packory/Packory.csproj src/Packory/
COPY workers/database-connector/Packory.DatabaseConnectorWorker.csproj workers/database-connector/
RUN dotnet restore workers/database-connector/Packory.DatabaseConnectorWorker.csproj
COPY src/Packory/ src/Packory/
COPY workers/database-connector/ workers/database-connector/
RUN dotnet publish src/Packory/Packory.csproj -c Release --no-restore -o /app/publish
RUN dotnet publish workers/database-connector/Packory.DatabaseConnectorWorker.csproj -c Release --no-restore -o /worker/publish

# Separate process/container with no Packory data, keyring, admin tokens or HTTP listener.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS database-worker
WORKDIR /worker
ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_HOME=/tmp
RUN mkdir -p /run/packory-db-ipc && chown -R "$APP_UID:$APP_UID" /run/packory-db-ipc
COPY --from=build /worker/publish/ .
USER $APP_UID
ENTRYPOINT ["dotnet", "Packory.DatabaseConnectorWorker.dll"]

# Keep the original default image target for Packory's existing CI/deployments.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS packory
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    PACKORY_DATA_DIRECTORY=/data \
    DOTNET_EnableDiagnostics=0
RUN mkdir -p /data/skill-scans/in /data/skill-scans/out /run/packory-db-ipc && \
    chown -R "$APP_UID:$APP_UID" /data /run/packory-db-ipc
COPY --from=build /app/publish/ .
USER $APP_UID
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Packory.dll"]
