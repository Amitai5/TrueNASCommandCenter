# Developer Guide

[Back to the main README](../../README.md) · [First-time setup guide](SETUP.md) · [Permission guide](PERMISSIONS.md)

This document covers local development, tests, container builds, project structure, publishing, and the TrueNAS middleware surface used by the application.

## Requirements

- .NET 10 SDK
- Docker for container builds
- Git

No running TrueNAS server, mail server, GitHub API, or webhook endpoint is required for the automated test suite.

## Repository structure

| Path | Purpose |
| --- | --- |
| `TrueNASCommandCenter/CommandCenterBlazor` | Main Blazor application, scheduler, persistence, notifications, and integrations |
| `TrueNASCommandCenter/Tests/TrueNASCommandCenter.Tests` | Hermetic MSTest unit and integration-style tests using fakes |
| `TrueNASCommandCenter/Dockerfile` | Multi-stage production image |
| `.github/workflows` | Pull-request validation and container publishing |
| `TrueNASCommandCenter/Docs` | Operator setup and developer documentation |

## Restore, build, and test

```bash
dotnet restore TrueNASCommandCenter/TrueNASCommandCenter.slnx
dotnet build TrueNASCommandCenter/TrueNASCommandCenter.slnx --no-restore
dotnet test TrueNASCommandCenter/TrueNASCommandCenter.slnx --no-build --no-restore
```

Tests use fake transports, HTTP handlers, TrueNAS mail requests, temporary SQLite databases, and deterministic `TimeProvider` values. They do not depend on the Internet, real TrueNAS middleware, the host file system outside temporary test directories, or wall-clock timing.

## Run locally

The application requires a writable data directory. In PowerShell:

```powershell
$env:DATA_PATH = "$PWD/.data"
$env:TRUENAS_WEBSOCKET_URL = "wss://truenas.example.test/api/current"
dotnet run --project TrueNASCommandCenter/CommandCenterBlazor/TrueNASCommandCenter.csproj --urls http://localhost:2600
```

In Bash:

```bash
DATA_PATH="$PWD/.data" TRUENAS_WEBSOCKET_URL="wss://truenas.example.test/api/current" dotnet run --project TrueNASCommandCenter/CommandCenterBlazor/TrueNASCommandCenter.csproj --urls http://localhost:2600
```

Open `http://localhost:2600` and use the first-launch wizard. Avoid using production API keys in development environments.

## Build the container

```bash
docker build --pull --file TrueNASCommandCenter/Dockerfile --tag truenas-command-center:local TrueNASCommandCenter
```

Run the local image with the same security restrictions used by the documented production deployment:

```bash
docker volume create update-manager-data

docker run --rm \
  --name truenas-command-center \
  --network host \
  --add-host truenas.example.test:192.0.2.10 \
  --env TRUENAS_APP_ID=truenas-command-center \
  --env TRUENAS_WEBSOCKET_URL=wss://truenas.example.test/api/current \
  --mount source=update-manager-data,target=/data \
  --read-only \
  --tmpfs /tmp:size=64m,mode=1777 \
  --cap-drop ALL \
  --security-opt no-new-privileges=true \
  truenas-command-center:local
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
- TrueNAS remains the lifecycle authority; inventory, host information, native alerts, OS update status, workload health, live app statistics, storage-pool status, logs, lifecycle actions, mail, upgrades, image refreshes, jobs, and rollbacks use JSON-RPC 2.0 middleware.
- Catalog discovery uses a dedicated read-only TrueNAS client contract and composite train/app identities so duplicate names in different trains cannot be merged accidentally.
- Catalog metadata, sanitized README text, safe external links, and optional public active-deployment telemetry are mapped into immutable discovery models before reaching Blazor components. Telemetry has its own bounded response, timeout, and cache and never gates catalog availability.
- Docker Hub custom-app discovery is an independent outbound-HTTPS integration. It uses bounded anonymous search, repository, and tag requests; validates repository identities and native filter values; sanitizes overview text; limits external logo hosts; and maps public data into immutable models before rendering.
- Complete inventory refresh and missing-app reconciliation always run before update evaluation.
- Per-app health incidents persist a single recovery-attempt marker so scheduled retries cannot loop.
- GitHub enrichment accepts only canonical public `github.com` sources, uses ETags and a 24-hour SQLite cache, and never gates TrueNAS operations.
- Scheduled and manual update executions are serialized.
- Policy evaluation fails closed when app state, semantic version parsing, or persistence is uncertain.
- API, Authorization, and secret-header values are encrypted before persistence.

### Frontend assets

The UI supports system-aware light and dark themes with a local manual override. Shared color tokens in `wwwroot/app.css` control text, surfaces, borders, inputs, badges, navigation states, and elevation; keep new components on these tokens so both themes retain readable contrast. The app-details page uses a bounded operations grid followed by a shared overview-card grid, which collapses to one column at mobile breakpoints. Avoid recreating an independently flowing full-height details rail because it leaves empty space beside shorter primary content.

The installable PWA metadata lives in `wwwroot/manifest.webmanifest`, pure browser classification and manual guidance in `wwwroot/pwa-install-guide.js`, installation and service-worker registration in `wwwroot/pwa.js`, and the network-first offline fallback in `wwwroot/service-worker.js`. Keep the service worker narrow: it may cache the explicit offline page and immutable brand assets, but it must not cache Blazor circuits, authenticated page responses, live TrueNAS data, logs, or lifecycle requests. The install handler must retain the visible manual fallback because `beforeinstallprompt` is not guaranteed, including on Samsung Internet. PWA installation testing requires HTTPS or the loopback `localhost` / `127.0.0.1` development exception. Run `node --test TrueNASCommandCenter/Tests/PwaInstallGuide.Tests.mjs` when changing platform detection or install directions.

Web Push subscription prompts are handled by the native click listener in `wwwroot/pwa.js` so browser user activation is preserved; the resulting subscription is persisted through the active Blazor circuit rather than an unauthenticated HTTP registration endpoint. `WebPushSubscriptionService` owns the encrypted-at-rest VAPID identity and device registry, `WebPushProtocolClient` signs payload-free wake-ups with VAPID using built-in .NET cryptography, and `WebPushNotificationSender` records delivery state and retires HTTP 404/410 endpoints. The service worker always renders a generic notification and opens the Dashboard. Do not add event details to the third-party push-service request without an explicit privacy and threat-model review. Full-recovery schema 5 includes the VAPID identity and subscriptions so restored devices remain usable.

Build-time static-asset compression is disabled because compressed Blazor responses produced corrupt-content failures in the target TrueNAS deployment. `Microsoft.AspNetCore.App.Internal.Assets` is a private build-only package reference so Linux restores include `_framework/blazor.web.js`; the project normalizes that package root, and the Dockerfile verifies that the raw asset and endpoint exist before publication.

## TrueNAS middleware methods

Catalog discovery (`CATALOG_READ`):

- `catalog.apps`
- `catalog.get_app_details`
- `app.similar`

Installed-app discovery and status (`APPS_READ`):

- `app.query`
- `app.get_instance`
- `app.outdated_docker_images`
- `app.upgrade_summary`
- `app.rollback_versions`
- `app.container_log_follow` through `core.subscribe`
- `app.stats` through `core.subscribe`

Optional read-only panels:

- `system.info` when the optional `READONLY_ADMIN` role is available
- `alert.list` when the optional `ALERT_LIST_READ` role is available
- `update.status` when the optional `SYSTEM_UPDATE_READ` role is available
- `pool.query` when the optional `POOL_READ` role is available

Execution and jobs (`APPS_WRITE` for lifecycle methods; authenticated core methods for transport and job coordination):

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

Do not infer catalog access from `APPS_READ`. TrueNAS assigns `catalog.apps`, `catalog.get_app_details`, and `app.similar` to `CATALOG_READ`. Keep [the permission guide](PERMISSIONS.md) synchronized whenever the middleware surface changes.

## Container publishing

[`VERSION`](../VERSION) is the canonical semantic release version. It is embedded into the .NET assembly, UI, startup logs, version endpoint, response headers, and OCI image metadata. Increment it before merging the next production release. Verification rejects malformed versions, and publishing rejects a version tag that already belongs to another commit.

GitHub Actions validates every branch and pull request without publishing it. A successful push to `production` publishes to GitHub Container Registry and creates the matching `v<version>` Git tag.

Published tags are:

- `latest` and `production` for the current `production` branch
- `1.2.3` and `1.2` from the repository `VERSION`
- `sha-<commit>` for an immutable commit build

After the first successful publish, the GitHub package must be public if anonymous TrueNAS pulls are required.

## Change checklist

1. Increment `VERSION` for every production release.
2. Keep changes localized and avoid adding dependencies without a concrete need.
3. Add or update MSTest coverage for behavior changes.
4. Run restore, build, and tests.
5. For frontend changes, publish and inspect the real static-asset responses as well as the rendered desktop and mobile UI.
6. Never commit API keys, notification secrets, encryption keys, databases, or user-specific deployment configuration.
