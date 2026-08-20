ARG DOTNET_SDK_VERSION=10.0.302
ARG ASPNET_VERSION=10.0.10

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
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
RUN test -s /app/wwwroot/_framework/blazor.web.js \
    || (echo "Blazor bootstrap asset is missing from the publish output." >&2; exit 1)
RUN grep -Fq '"Route":"_framework/blazor.web.js"' /app/TrueNasUpdateManager.staticwebassets.endpoints.json \
    || (echo "Blazor bootstrap endpoint is missing from the static web assets manifest." >&2; exit 1)

FROM mcr.microsoft.com/dotnet/aspnet:${ASPNET_VERSION} AS runtime
WORKDIR /app

RUN mkdir -p /data && chown app:app /data
COPY --from=build --chown=app:app /app ./

USER app
ENV ASPNETCORE_HTTP_PORTS=2600 \
    DATA_PATH=/data \
    DOTNET_EnableDiagnostics=0
EXPOSE 2600
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "TrueNasUpdateManager.dll"]
