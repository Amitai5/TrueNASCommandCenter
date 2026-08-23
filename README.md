# TrueNAS App Manager

[![Production build](https://img.shields.io/github/actions/workflow/status/Amitai5/TrueNASAppManager/publish-container.yml?branch=production&style=for-the-badge&label=production)](https://github.com/Amitai5/TrueNASAppManager/actions/workflows/publish-container.yml)
[![Container image](https://img.shields.io/badge/GHCR-latest-2496ED?style=for-the-badge&logo=docker&logoColor=white)](https://github.com/Amitai5/TrueNASAppManager/pkgs/container/truenasappmanager)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![TrueNAS](https://img.shields.io/badge/TrueNAS-25.10%2B-0095D5?style=for-the-badge&logo=truenas&logoColor=white)](https://www.truenas.com/)
[![License](https://img.shields.io/badge/license-MIT-16A34A?style=for-the-badge)](LICENSE)

TrueNAS App Manager is a single-container web application for TrueNAS Community Edition / SCALE 25.10 and later. It discovers installed apps, manages their lifecycle, applies explicit per-app update policies, schedules safe checks and updates, records history, and sends optional email or webhook notifications.

TrueNAS remains the lifecycle authority. The manager uses the TrueNAS JSON-RPC 2.0 middleware API for discovery, start, stop, restart, catalog upgrades, image refreshes, job monitoring, and rollbacks. It never controls Docker directly.

## Features

- Explicit **Auto Update**, **Notify Only**, **Ignore**, and fail-closed **Unconfigured** policies
- Conservative Any Version, Minor + Patch, and Patch-only version scopes
- Catalog upgrades and image-only refreshes
- Start, stop, and restart controls backed by TrueNAS jobs
- Five-field cron scheduling with IANA timezones
- Sequential updates with TrueNAS job waiting and post-update verification
- Manual rollback to versions reported by TrueNAS
- Per-app health policies: Ignore, Notify Only, or one automatic restart attempt plus notification
- Top-level and container health, maintenance mode, recovery notifications, and lifecycle audit history
- Prominent published ports, route-aware local/remote Web UI links, and formatted on-demand live container logs with copy and fullscreen controls
- Portable secret-free JSON backups and optional password-encrypted full configuration backups
- Optional TrueNAS-native email and generic webhook notifications
- Optional public GitHub repository facts with 24-hour ETag caching and no token
- Encrypted API, Authorization, and secret-header values
- Detailed run, attempt, skip, failure, rollback, and notification history
- Responsive system-aware light/dark web UI with a persistent manual toggle
- SQLite persistence in a dedicated `/data` volume
- Liveness and readiness endpoints

## Documentation

- **[First-Time Setup Guide](docs/SETUP.md)** — service account, `APPS_READ` / `APPS_WRITE` privileges, API key, connection fields, wizard steps, and troubleshooting
- **[Developer Guide](docs/DEVELOPMENT.md)** — local builds, tests, container development, architecture, publishing, and TrueNAS middleware methods
- **In-app setup help** — after installation, open `http://<truenas-address>:2600/help` or select **Help** in the web UI

## Requirements

- TrueNAS Community Edition / SCALE 25.10 or later
- A configured TrueNAS Apps storage pool
- A service account and user-linked API key with `APPS_READ` and `APPS_WRITE`
- A trusted LAN/VPN, or an authenticated reverse proxy in front of the web UI

The application does not include its own user accounts or RBAC. Do not expose it directly to an untrusted network.

## Installation

### TrueNAS Custom App via YAML

This is the recommended TrueNAS installation method.

1. Open **Apps → Discover**.
2. Open the menu beside **Custom App** and select **Install via YAML**.
3. Enter an application name such as `truenas-app-manager`.
4. Paste the following Compose configuration into **Custom Config**.
5. Click **Save** and wait for the app to report a running state.

```yaml
services:
  truenas-app-manager:
    cap_drop:
      - ALL
    environment:
      ASPNETCORE_HTTP_PORTS: 2600
      DATA_PATH: /data
      TRUENAS_APP_ID: truenas-app-manager
      TRUENAS_WEBSOCKET_URL: wss://truenas.local/api/current
    extra_hosts:
      - truenas.local:10.0.0.21
    image: ghcr.io/amitai5/truenasappmanager:latest
    labels:
      org.opencontainers.image.description: Manage, monitor, inspect, and safely update TrueNAS apps.
      org.opencontainers.image.source: https://github.com/Amitai5/TrueNASAppManager
      org.opencontainers.image.title: TrueNAS App Manager
      org.opencontainers.image.url: https://github.com/Amitai5/TrueNASAppManager
    network_mode: host
    pull_policy: always
    read_only: true
    restart: unless-stopped
    security_opt:
      - no-new-privileges:true
    tmpfs:
      - /tmp:size=64m,mode=1777
    volumes:
      - update-manager-data:/data
volumes:
  update-manager-data: null
x-app-port: 2600
x-notes: >-
  TrueNAS App Manager monitors app health, exposes ports and Web UI links, streams container logs,
  and refreshes inventory before every scheduled update check. Open the Web UI to finish setup.
x-portals:
  - host: 0.0.0.0
    name: Web UI
    path: /
    port: 2600
    scheme: http
```

Open `http://<truenas-address>:2600`. Custom apps installed from YAML might not receive a **Web UI** button in TrueNAS, so navigate to the address directly.

This configuration uses the current TrueNAS Web UI address, `10.0.0.21`. If that address changes, update the complete YAML's `extra_hosts` value before redeploying. Prefer a DHCP reservation or static address. If your certificate uses a different hostname, replace `truenas.local` in both `extra_hosts` and `TRUENAS_WEBSOCKET_URL`.

Host networking is the reliable default because it lets the manager reach the TrueNAS Web UI address without Docker bridge or LAN hairpin failures. Host mode does not use Docker port publishing; the ASP.NET listener binds directly to the host network.

`ASPNETCORE_HTTP_PORTS` controls the listener, while `x-app-port` records the same port for the TrueNAS configuration. If `2600` is already in use, change both values to the same unused port above `1023`, save the complete YAML, and open that port in the browser.

### Optional bridge-network deployment

Bridge networking makes port `2600` appear in TrueNAS workload metadata, but some TrueNAS hosts cannot route a custom-app bridge back to their own Web UI address. Use this complete alternative only when **Test connection** succeeds; otherwise return to the host-network YAML above.

```yaml
services:
  truenas-app-manager:
    cap_drop:
      - ALL
    environment:
      ASPNETCORE_HTTP_PORTS: 2600
      DATA_PATH: /data
      TRUENAS_APP_ID: truenas-app-manager
      TRUENAS_WEBSOCKET_URL: wss://truenas.local/api/current
    extra_hosts:
      - truenas.local:10.0.0.21
    image: ghcr.io/amitai5/truenasappmanager:latest
    labels:
      org.opencontainers.image.description: Manage, monitor, inspect, and safely update TrueNAS apps.
      org.opencontainers.image.source: https://github.com/Amitai5/TrueNASAppManager
      org.opencontainers.image.title: TrueNAS App Manager
      org.opencontainers.image.url: https://github.com/Amitai5/TrueNASAppManager
    ports:
      - protocol: tcp
        published: 2600
        target: 2600
    pull_policy: always
    read_only: true
    restart: unless-stopped
    security_opt:
      - no-new-privileges:true
    tmpfs:
      - /tmp:size=64m,mode=1777
    volumes:
      - update-manager-data:/data
volumes:
  update-manager-data: null
x-app-port: 2600
x-notes: >-
  TrueNAS App Manager monitors app health, exposes ports and Web UI links, streams container logs,
  and refreshes inventory before every scheduled update check. Open the Web UI to finish setup.
x-portals:
  - host: 0.0.0.0
    name: Web UI
    path: /
    port: 2600
    scheme: http
```

### Docker

Create a persistent volume and run the published image:

```bash
docker volume create update-manager-data

docker run --detach \
  --name truenas-app-manager \
  --restart unless-stopped \
  --network host \
  --add-host truenas.local:10.0.0.21 \
  --env TRUENAS_APP_ID=truenas-app-manager \
  --env TRUENAS_WEBSOCKET_URL=wss://truenas.local/api/current \
  --mount source=update-manager-data,target=/data \
  --read-only \
  --tmpfs /tmp:size=64m,mode=1777 \
  --cap-drop ALL \
  --security-opt no-new-privileges=true \
  ghcr.io/amitai5/truenasappmanager:latest
```

If the TrueNAS Web UI address changes from `10.0.0.21`, update `--add-host`, then open `http://localhost:2600` and follow the [First-Time Setup Guide](docs/SETUP.md).

## First launch

The wizard uses the secure TrueNAS endpoint configured in the deployment YAML but does not preconfigure credentials, schedule, timezone, policy, notification target, or notification event.

1. Enter a dedicated TrueNAS service account and test its API key. Keep certificate verification enabled when the certificate is trusted and covers the hostname in `TRUENAS_WEBSOCKET_URL`.
2. Optionally configure scheduled checks and updates.
3. Optionally configure TrueNAS-native email or webhook notifications.
4. Discover installed apps and assign an explicit policy to each one.

The **Continue** button on the connection step remains disabled until **Test connection** succeeds. See the [setup guide](docs/SETUP.md) or the in-app **Help** page for account, certificate, connection, and browser troubleshooting.

## App access, logs, and configuration backups

Each app policy has separate **Local Web UI URL** and **Remote Web UI URL** fields. When the manager is opened through `truenas.local`, localhost, or a private/link-local address, its Web UI buttons use the local route. When it is opened through a public domain such as `apps.example.com`, the buttons use the explicitly configured remote route. Remote addresses are never guessed. The global **Local TrueNAS Web UI host** setting remains available for rewriting TrueNAS-provided portals and pairing local published ports with a stable hostname.

The app-details page prioritizes operations. It shows the current route, ports, health, workloads, versions, and lifecycle controls around a large live-log workspace. Logs contain at most the latest 500 loaded lines, stay in browser memory, and can be selected manually, copied as ISO-8601 text, or opened fullscreen. A successfully completed `permissions` helper workload is shown as **Exited normally** and does not degrade an otherwise running app.

Open **Settings → Backup & restore** for portable configuration backups:

- **Safe JSON** excludes API keys and webhook secrets. Importing it retains any secrets already stored in the destination.
- **Encrypted JSON** includes stored secrets and protects them with password-derived AES-256-GCM authenticated encryption.
- Imports validate the complete file before a transactional merge. Listed app configurations are restored by app ID, unlisted apps and existing history remain unchanged, and undiscovered app policies are held until the next inventory refresh.

Portable backups intentionally exclude inventory, logs, health incidents, GitHub cache, notifications, and update history. Continue backing up the persistent `/data` volume for complete disaster recovery. If `APP_ENCRYPTION_KEY` is configured externally, back it up separately.

## Runtime configuration

| Variable | Required | Description |
| --- | --- | --- |
| `ASPNETCORE_HTTP_PORTS` | Supplied by the image | HTTP listen port; the production image defaults to `2600` |
| `DATA_PATH` | Supplied by the image | Writable directory for SQLite and generated encryption material |
| `TRUENAS_WEBSOCKET_URL` | Yes | Secure TrueNAS JSON-RPC endpoint; must be an absolute `wss://` URL ending in `/api/current` |
| `APP_ENCRYPTION_KEY` | No | Base64-encoded 32-byte external key for stronger secret-key separation |
| `TRUENAS_APP_ID` | No | Manager app ID used to block attempts to update itself |

The endpoint is deployment configuration rather than a browser-editable setting. The app validates it at startup, requires `wss://`, rejects embedded credentials, queries, and fragments, and ignores legacy endpoint values stored in the database.

Generate an optional external encryption key with:

```bash
openssl rand -base64 32
```

Back up the external key separately from `/data`. Losing it makes saved secrets unrecoverable.

## Safe-by-default behavior

- New apps are discovered as **Unconfigured** and never update automatically.
- Only one check/update run can execute at a time.
- Scheduled and Check & Update runs process apps sequentially.
- **Refresh Apps** reconciles the installed app list, ports, portals, containers, and health without installing updates.
- Every manual and scheduled update check refreshes the complete inventory first.
- GitHub enrichment is opt-in, cached, concurrency-limited, and never controls or blocks TrueNAS operations.
- `action_required` always blocks unattended execution.
- Running apps can auto-update; stopped and crashed apps require manual confirmation.
- Transitional states are skipped.
- Version parsing fails closed outside Any Version scope.
- Persistence failure stops unattended execution before an app lifecycle call.
- TrueNAS job success is followed by state, version, and image verification.

Host networking is intentionally enabled so this single-purpose manager can reach TrueNAS middleware. Keep privileged mode disabled, do not mount `/var/run/docker.sock`, retain the dropped capabilities and read-only root filesystem, and grant write access only to `/data`.

## Updating

The `latest` and `production` image tags track the current `production` branch. Version tags such as `1.2.3` can be used to pin a deployment. The recommended YAML uses `pull_policy: always`, which makes Compose check GHCR whenever TrueNAS applies or recreates the app instead of trusting a cached tag.

In **Apps → Configuration → Settings**, keep **Check for docker image updates** enabled. To apply an available image, update/redeploy the custom app or edit its YAML and save without changing the `/data` volume. `pull_policy: always` does not restart a running container by itself; it takes effect when TrueNAS reapplies the Compose project. After an update, hard-refresh the browser if it has cached older frontend assets.

## Health endpoints

- `/health/live` — process liveness, independent of TrueNAS connectivity
- `/health/ready` — application initialization and SQLite connectivity

Temporary TrueNAS downtime does not make application readiness fail.

## TrueNAS custom-app metadata limits

The YAML supplies a Web UI portal, operator notes, and OCI labels. TrueNAS still identifies YAML installs as custom apps, so its native **Application Info** card can continue to show a generic icon, `App Version: custom`, and `Source: N/A`. TrueNAS App Manager displays the richer TrueNAS workload data and optional GitHub facts inside its own app-details page; it does not create unsupported catalog metadata files or catalog routes.
