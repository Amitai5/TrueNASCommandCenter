# Developer Guide

[Back to the main README](../README.md) · [First-time setup guide](SETUP.md)

This document covers local development, tests, container builds, project structure, publishing, and the TrueNAS middleware surface used by the application.

## Requirements

- .NET 10 SDK
- Docker for container builds
- Git

No running TrueNAS server, mail server, GitHub API, or webhook endpoint is required for the automated test suite.

## Repository structure

| Path | Purpose |
| --- | --- |
| `src/TrueNasAppManager` | Blazor application, scheduler, persistence, notifications, and integrations |
| `tests/TrueNasAppManager.Tests` | Hermetic MSTest unit and integration-style tests using fakes |
| `Dockerfile` | Multi-stage production image |
| `.github/workflows` | Pull-request validation and container publishing |
| `docs` | Operator setup and developer documentation |

## Restore, build, and test

```bash
dotnet restore TrueNasAppManager.slnx
dotnet build TrueNasAppManager.slnx --no-restore
dotnet test TrueNasAppManager.slnx --no-build --no-restore
```

Tests use fake transports, HTTP handlers, TrueNAS mail requests, temporary SQLite databases, and deterministic `TimeProvider` values. They do not depend on the Internet, real TrueNAS middleware, the host file system outside temporary test directories, or wall-clock timing.

## Run locally

The application requires a writable data directory. In PowerShell:

```powershell
$env:DATA_PATH = "$PWD/.data"
$env:TRUENAS_WEBSOCKET_URL = "wss://truenas.example.test/api/current"
dotnet run --project src/TrueNasAppManager/TrueNasAppManager.csproj --urls http://localhost:2600
```

In Bash:

```bash
DATA_PATH="$PWD/.data" TRUENAS_WEBSOCKET_URL="wss://truenas.example.test/api/current" dotnet run --project src/TrueNasAppManager/TrueNasAppManager.csproj --urls http://localhost:2600
```

Open `http://localhost:2600` and use the first-launch wizard. Avoid using production API keys in development environments.

## Build the container

```bash
docker build --pull --tag truenas-app-manager:local .
```

Run the local image with the same security restrictions used by the documented production deployment:

```bash
docker volume create update-manager-data

docker run --rm \
  --name truenas-app-manager \
  --network host \
  --add-host truenas.example.test:192.0.2.10 \
  --env TRUENAS_APP_ID=truenas-app-manager \
  --env TRUENAS_WEBSOCKET_URL=wss://truenas.example.test/api/current \
  --mount source=update-manager-data,target=/data \
  --read-only \
  --tmpfs /tmp:size=64m,mode=1777 \
  --cap-drop ALL \
  --security-opt no-new-privileges=true \
  truenas-app-manager:local
```

Replace the documentation-only address `192.0.2.10` with the target TrueNAS Web UI IPv4 address before an end-to-end test. Host networking matches production, while the explicit host mapping makes local hostname resolution deterministic. The unit test suite uses a fake WebSocket transport and does not require a reachable endpoint.

## Runtime configuration

| Variable | Required | Purpose |
| --- | --- | --- |
| `ASPNETCORE_HTTP_PORTS` | Container default supplied | HTTP listen port; production image defaults to `2600` |
| `DATA_PATH` | Container default supplied | Writable directory for SQLite and generated encryption material |
| `TRUENAS_WEBSOCKET_URL` | Yes | Absolute `wss://` TrueNAS JSON-RPC endpoint ending in `/api/current` |
| `APP_ENCRYPTION_KEY` | No | Base64-encoded 32-byte external key for stronger secret-key separation |
| `TRUENAS_APP_ID` | No | App ID used to prevent the manager from updating itself |

`TRUENAS_WEBSOCKET_URL` is deployment configuration, not an end-user setting. The application fails fast when it is missing or not an absolute `wss://` URL ending in `/api/current`. Never include credentials, query values, or fragments in it.

Do not add secrets, server-specific URLs, schedules, policies, recipients, or host paths to source-controlled configuration.

## Architecture notes

- The web application uses interactive server-side Blazor on ASP.NET Core .NET 10.
- SQLite state is stored under `DATA_PATH`.
- TrueNAS remains the lifecycle authority; inventory, workload health, logs, lifecycle actions, mail, upgrades, image refreshes, jobs, and rollbacks use JSON-RPC 2.0 middleware.
- Complete inventory refresh and missing-app reconciliation always run before update evaluation.
- Per-app health incidents persist a single recovery-attempt marker so scheduled retries cannot loop.
- GitHub enrichment accepts only canonical public `github.com` sources, uses ETags and a 24-hour SQLite cache, and never gates TrueNAS operations.
- Scheduled and manual update executions are serialized.
- Policy evaluation fails closed when app state, semantic version parsing, or persistence is uncertain.
- API, Authorization, and secret-header values are encrypted before persistence.

### Frontend assets

The UI supports system-aware light and dark themes with a local manual override. Build-time static-asset compression is disabled because compressed Blazor responses produced corrupt-content failures in the target TrueNAS deployment. `Microsoft.AspNetCore.App.Internal.Assets` is a private build-only package reference so Linux restores include `_framework/blazor.web.js`; the project normalizes that package root, and the Dockerfile verifies that the raw asset and endpoint exist before publication.

## TrueNAS middleware methods

Discovery and status:

- `app.query`
- `app.get_instance`
- `app.outdated_docker_images`
- `app.upgrade_summary`
- `app.rollback_versions`
- `app.container_log_follow` through `core.subscribe`

Execution and jobs:

- `app.upgrade`
- `app.pull_images`
- `app.rollback`
- `core.job_wait`
- `core.subscribe`
- `core.unsubscribe`
- `core.ping`
- `auth.login_ex`
- `mail.send`

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
