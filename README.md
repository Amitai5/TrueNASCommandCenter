# TrueNAS App Update Manager

TrueNAS App Update Manager is a single-container web application for TrueNAS Community Edition / SCALE 25.10 and later. It discovers installed apps, applies explicit per-app update policies, schedules safe checks and updates, records history, and sends optional email or webhook notifications.

TrueNAS remains the lifecycle authority. The manager uses the TrueNAS JSON-RPC 2.0 middleware API for discovery, catalog upgrades, image refreshes, job monitoring, and rollbacks. It never controls Docker directly.

## Features

- Explicit **Auto Update**, **Notify Only**, **Ignore**, and fail-closed **Unconfigured** policies
- Conservative Any Version, Minor + Patch, and Patch-only version scopes
- Catalog upgrades and image-only refreshes
- Five-field cron scheduling with IANA timezones
- Sequential updates with TrueNAS job waiting and post-update verification
- Manual rollback to versions reported by TrueNAS
- Optional email and generic webhook notifications
- Encrypted API, SMTP, Authorization, and secret-header values
- Detailed run, attempt, skip, failure, rollback, and notification history
- Responsive system-aware light/dark web UI with a persistent manual toggle
- SQLite persistence in a dedicated `/data` volume
- Liveness and readiness endpoints

## Documentation

- **[First-Time Setup Guide](docs/SETUP.md)** — service account, `APPS_READ` / `APPS_WRITE` privileges, API key, connection fields, wizard steps, and troubleshooting
- **[Developer Guide](docs/DEVELOPMENT.md)** — local builds, tests, container development, architecture, publishing, and TrueNAS middleware methods
- **In-app setup help** — after installation, open `http://<truenas-address>:1800/help` or select **Help** in the web UI

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
3. Enter an application name such as `truenas-update-manager`.
4. Paste the following Compose configuration into **Custom Config**.
5. Click **Save** and wait for the app to report a running state.

```yaml
services:
  truenas-update-manager:
    image: ghcr.io/amitai5/truenasautoupdater:latest
    pull_policy: always
    restart: unless-stopped
    ports:
      - "1800:1800"
    environment:
      ASPNETCORE_HTTP_PORTS: 1800
      DATA_PATH: /data
    volumes:
      - update-manager-data:/data
    read_only: true
    tmpfs:
      - /tmp:size=64m,mode=1777
    cap_drop:
      - ALL
    security_opt:
      - no-new-privileges:true

volumes:
  update-manager-data:
```

Open `http://<truenas-address>:1800`. Custom apps installed from YAML might not receive a **Web UI** button in TrueNAS, so navigate to the address directly.

Port `1800` is above Linux's privileged port range, so the non-root container can listen on it directly. If host port `1800` is already in use, change only the first number in `"1800:1800"`, for example `"8180:1800"`, and open that host port in the browser.

### Docker

Create a persistent volume and run the published image:

```bash
docker volume create update-manager-data

docker run --detach \
  --name truenas-update-manager \
  --restart unless-stopped \
  --publish 1800:1800 \
  --mount source=update-manager-data,target=/data \
  --read-only \
  --tmpfs /tmp:size=64m,mode=1777 \
  --cap-drop ALL \
  --security-opt no-new-privileges=true \
  ghcr.io/amitai5/truenasautoupdater:latest
```

Open `http://localhost:1800` and follow the [First-Time Setup Guide](docs/SETUP.md).

## First launch

The wizard does not preconfigure a connection, schedule, timezone, policy, hostname, notification target, or notification event.

1. Connect a dedicated TrueNAS service account and test its API key.
2. Optionally configure scheduled checks and updates.
3. Optionally configure email or webhook notifications.
4. Discover installed apps and assign an explicit policy to each one.

The **Continue** button on the connection step remains disabled until **Test connection** succeeds. See the [setup guide](docs/SETUP.md) or the in-app **Help** page for account, certificate, connection, and browser troubleshooting.

## Runtime configuration

| Variable | Required | Description |
| --- | --- | --- |
| `ASPNETCORE_HTTP_PORTS` | Supplied by the image | HTTP listen port; the production image uses `1800` |
| `DATA_PATH` | Supplied by the image | Writable directory for SQLite and generated encryption material |
| `APP_ENCRYPTION_KEY` | No | Base64-encoded 32-byte external key for stronger secret-key separation |
| `TRUENAS_APP_ID` | No | Manager app ID used to block attempts to update itself |

Generate an optional external encryption key with:

```bash
openssl rand -base64 32
```

Back up the external key separately from `/data`. Losing it makes saved secrets unrecoverable.

## Safe-by-default behavior

- New apps are discovered as **Unconfigured** and never update automatically.
- Only one check/update run can execute at a time.
- Scheduled and Check & Update runs process apps sequentially.
- **Check Now** never installs updates.
- `action_required` always blocks unattended execution.
- Running apps can auto-update; stopped and crashed apps require manual confirmation.
- Transitional states are skipped.
- Version parsing fails closed outside Any Version scope.
- Persistence failure stops unattended execution before an app lifecycle call.
- TrueNAS job success is followed by state, version, and image verification.

Keep privileged mode and host networking disabled, do not mount `/var/run/docker.sock`, and grant write access only to `/data`.

## Updating

The `latest` and `production` image tags track the current `production` branch. Version tags such as `1.2.3` can be used to pin a deployment. The recommended YAML uses `pull_policy: always`, which makes Compose check GHCR whenever TrueNAS applies or recreates the app instead of trusting a cached tag.

In **Apps → Configuration → Settings**, keep **Check for docker image updates** enabled. To apply an available image, update/redeploy the custom app or edit its YAML and save without changing the `/data` volume. `pull_policy: always` does not restart a running container by itself; it takes effect when TrueNAS reapplies the Compose project. After an update, hard-refresh the browser if it has cached older frontend assets.

## Health endpoints

- `/health/live` — process liveness, independent of TrueNAS connectivity
- `/health/ready` — application initialization and SQLite connectivity

Temporary TrueNAS downtime does not make application readiness fail.
