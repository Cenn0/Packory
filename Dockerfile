FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Packory/Packory.csproj src/Packory/
RUN dotnet restore src/Packory/Packory.csproj
COPY src/Packory/ src/Packory/
RUN dotnet publish src/Packory/Packory.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    PACKORY_DATA_DIRECTORY=/data \
    DOTNET_EnableDiagnostics=0
RUN mkdir -p /data && chown "$APP_UID:$APP_UID" /data
COPY --from=build /app/publish/ .
USER $APP_UID
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Packory.dll"]
