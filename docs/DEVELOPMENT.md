# Developer Guide

[Back to the main README](../README.md) · [First-time setup guide](SETUP.md)

This document covers local development, tests, container builds, project structure, publishing, and the TrueNAS middleware surface used by the application.

## Requirements

- .NET 10 SDK
- Docker for container builds
- Git

No running TrueNAS server, SMTP server, or webhook endpoint is required for the automated test suite.

## Repository structure

| Path | Purpose |
| --- | --- |
| `src/TrueNasUpdateManager` | Blazor application, scheduler, persistence, notifications, and integrations |
| `tests/TrueNasUpdateManager.Tests` | Hermetic MSTest unit and integration-style tests using fakes |
| `Dockerfile` | Multi-stage production image |
| `.github/workflows` | Pull-request validation and container publishing |
| `docs` | Operator setup and developer documentation |

## Restore, build, and test

```bash
dotnet restore TrueNasUpdateManager.slnx
dotnet build TrueNasUpdateManager.slnx --no-restore
dotnet test TrueNasUpdateManager.slnx --no-build --no-restore
```

Tests use fake transports, HTTP handlers, SMTP message construction, temporary SQLite databases, and deterministic `TimeProvider` values. They do not depend on the Internet, real TrueNAS middleware, the host file system outside temporary test directories, or wall-clock timing.

## Run locally

The application requires a writable data directory. In PowerShell:

```powershell
$env:DATA_PATH = "$PWD/.data"
dotnet run --project src/TrueNasUpdateManager/TrueNasUpdateManager.csproj --urls http://localhost:8080
```

In Bash:

```bash
DATA_PATH="$PWD/.data" dotnet run --project src/TrueNasUpdateManager/TrueNasUpdateManager.csproj --urls http://localhost:8080
```

Open `http://localhost:8080` and use the first-launch wizard. Avoid using production API keys in development environments.

## Build the container

```bash
docker build --pull --tag truenas-update-manager:local .
```

Run the local image with the same security restrictions used by the documented production deployment:

```bash
docker volume create update-manager-data

docker run --rm \
  --name truenas-update-manager \
  --publish 1000:8080 \
  --mount source=update-manager-data,target=/data \
  --read-only \
  --tmpfs /tmp:size=64m,mode=1777 \
  --cap-drop ALL \
  --security-opt no-new-privileges=true \
  truenas-update-manager:local
```

## Runtime configuration

| Variable | Required | Purpose |
| --- | --- | --- |
| `ASPNETCORE_URLS` | Container default supplied | HTTP listen address; production image uses `http://0.0.0.0:8080` |
| `DATA_PATH` | Container default supplied | Writable directory for SQLite and generated encryption material |
| `APP_ENCRYPTION_KEY` | No | Base64-encoded 32-byte external key for stronger secret-key separation |
| `TRUENAS_APP_ID` | No | App ID used to prevent the manager from updating itself |

Do not add secrets, server-specific URLs, schedules, policies, recipients, or host paths to source-controlled configuration.

## Architecture notes

- The web application uses interactive server-side Blazor on ASP.NET Core .NET 10.
- SQLite state is stored under `DATA_PATH`.
- TrueNAS remains the lifecycle authority; all discovery, upgrades, image refreshes, jobs, and rollbacks use JSON-RPC 2.0 middleware.
- Scheduled and manual update executions are serialized.
- Policy evaluation fails closed when app state, semantic version parsing, or persistence is uncertain.
- API, SMTP, Authorization, and secret-header values are encrypted before persistence.

### Frontend assets

The UI supports system-aware light and dark themes with a local manual override. Build-time static-asset compression is disabled because compressed Blazor responses produced corrupt-content failures in the target TrueNAS deployment. The project normalizes the framework asset package root so Linux publishes include `_framework/blazor.web.js`, and the Dockerfile verifies that the raw asset and its endpoint are present before an image can be published.

## TrueNAS middleware methods

Discovery and status:

- `app.query`
- `app.get_instance`
- `app.outdated_docker_images`
- `app.upgrade_summary`
- `app.rollback_versions`

Execution and jobs:

- `app.upgrade`
- `app.pull_images`
- `app.rollback`
- `core.job_wait`
- `core.ping`
- `auth.login_ex`

API DTOs are intentionally narrow. Validate the installed TrueNAS API schemas when adding support for a new TrueNAS release.

## Container publishing

GitHub Actions validates pull requests by building the image without publishing it. Pushes to `production` publish to GitHub Container Registry.

Published tags are:

- `latest` and `production` for the current `production` branch
- `1.2.3` and `1.2` for a Git tag such as `v1.2.3`
- `sha-<commit>` for an immutable commit build

After the first successful publish, the GitHub package must be public if anonymous TrueNAS pulls are required.

## Change checklist

1. Keep changes localized and avoid adding dependencies without a concrete need.
2. Add or update MSTest coverage for behavior changes.
3. Run restore, build, and tests.
4. For frontend changes, publish and inspect the real static-asset responses as well as the rendered desktop and mobile UI.
5. Never commit API keys, notification secrets, encryption keys, databases, or user-specific deployment configuration.
