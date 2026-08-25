ARG DOTNET_SDK_VERSION=10.0.302
ARG ASPNET_VERSION=10.0.10
ARG APP_VERSION=1.3.1

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
ARG APP_VERSION
WORKDIR /source

COPY TrueNasAppManager.slnx ./
COPY src/TrueNasAppManager/TrueNasAppManager.csproj src/TrueNasAppManager/
RUN dotnet restore src/TrueNasAppManager/TrueNasAppManager.csproj

COPY src/TrueNasAppManager/ src/TrueNasAppManager/
RUN dotnet publish src/TrueNasAppManager/TrueNasAppManager.csproj \
    --configuration Release \
    --no-restore \
    --output /app \
    /p:UseAppHost=false \
    /p:Version=${APP_VERSION} \
    /p:InformationalVersion=${APP_VERSION}
RUN test -s /app/wwwroot/_framework/blazor.web.js \
    || (echo "Blazor bootstrap asset is missing from the publish output." >&2; exit 1)
RUN grep -Fq '"Route":"_framework/blazor.web.js"' /app/TrueNasAppManager.staticwebassets.endpoints.json \
    || (echo "Blazor bootstrap endpoint is missing from the static web assets manifest." >&2; exit 1)

FROM mcr.microsoft.com/dotnet/aspnet:${ASPNET_VERSION} AS runtime
ARG APP_VERSION
WORKDIR /app

LABEL org.opencontainers.image.title="TrueNAS App Manager" \
      org.opencontainers.image.description="Manage, monitor, inspect, and safely update TrueNAS apps." \
      org.opencontainers.image.source="https://github.com/Amitai5/TrueNASAppManager" \
      org.opencontainers.image.url="https://github.com/Amitai5/TrueNASAppManager" \
      org.opencontainers.image.version="${APP_VERSION}"

RUN mkdir -p /data && chown app:app /data
COPY --from=build --chown=app:app /app ./

USER app
ENV ASPNETCORE_HTTP_PORTS=2600 \
    DATA_PATH=/data \
    APP_VERSION=${APP_VERSION} \
    DOTNET_EnableDiagnostics=0
EXPOSE 2600
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "TrueNasAppManager.dll"]
