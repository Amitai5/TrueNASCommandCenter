FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /source

COPY TrueNasUpdateManager.slnx ./
COPY src/TrueNasUpdateManager/TrueNasUpdateManager.csproj src/TrueNasUpdateManager/
RUN dotnet restore src/TrueNasUpdateManager/TrueNasUpdateManager.csproj

COPY src/TrueNasUpdateManager/ src/TrueNasUpdateManager/
RUN dotnet publish src/TrueNasUpdateManager/TrueNasUpdateManager.csproj \
    --configuration Release \
    --no-restore \
    --output /app \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10 AS runtime
WORKDIR /app

RUN mkdir -p /data && chown app:app /data
COPY --from=build --chown=app:app /app ./

USER app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DATA_PATH=/data \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "TrueNasUpdateManager.dll"]
