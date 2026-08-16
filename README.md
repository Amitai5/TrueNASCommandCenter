# TrueNAS App Update Manager

A single-container update manager for TrueNAS SCALE / Community Edition 25.10+. It discovers installed apps, applies explicit per-app policies, schedules safe checks and updates, records history, and sends email or generic webhook notifications.

TrueNAS remains the lifecycle authority. Every catalog upgrade, image refresh, and rollback uses the TrueNAS JSON-RPC 2.0 middleware API. The manager never invokes Docker directly.

## V1 features

- Blazor Web App on ASP.NET Core .NET 10
- SQLite persistence at `/data/app.db`
- TrueNAS JSON-RPC 2.0 over `ws(s)://…/api/current`
- Unconfigured, Auto Update, Notify Only, and Ignore app policies
- Conservative Any / Minor + Patch / Patch version scope
- Catalog upgrades and image-only refreshes
- Internal 5-field cron scheduling with IANA timezones
- Sequential updates with TrueNAS job waiting and post-update verification
- Manual rollback only to versions returned by TrueNAS
- Deduplicated Email and Generic Webhook notifications
- Encrypted API, SMTP, Authorization, and secret-header values
- Run, attempt, skip, failure, rollback, and notification history
- Responsive light-first UI
- Liveness and readiness endpoints

## Build locally

The recommended build uses the included multi-stage Dockerfile:

```bash
docker build --pull --tag truenas-update-manager:local .
```

To build and start it with the hardened local Compose configuration instead:

```bash
docker compose up --build --detach
```

Open `http://localhost:8080` and complete the first-launch wizard. The example uses a named volume and contains no server-specific hostnames, schedules, policies, recipients, endpoints, or host paths.

## Deploy the published image

GitHub Actions publishes the image to the GitHub Container Registry:

```bash
docker pull ghcr.io/amitai5/truenasautoupdater:latest
```

Available tags are:

- `latest` and `production` for the current `production` branch
- `1.2.3` and `1.2` for a Git tag such as `v1.2.3`
- `sha-<commit>` for an immutable commit build

The workflow validates pull requests by building the image without publishing it. After the first successful publish, make the package public in its GitHub package settings if anonymous pulls are required.

### Docker

Create a persistent volume, then run the published image with the same restrictions as the supplied Compose configuration:

```bash
docker volume create update-manager-data

docker run --detach \
  --name truenas-update-manager \
  --restart unless-stopped \
  --publish 8080:8080 \
  --mount source=update-manager-data,target=/data \
  --read-only \
  --tmpfs /tmp:size=64m,mode=1777 \
  --cap-drop ALL \
  --security-opt no-new-privileges=true \
  ghcr.io/amitai5/truenasautoupdater:latest
```

Open `http://localhost:8080`. To upgrade, pull the desired tag and recreate the container with the same `/data` volume.

### TrueNAS Custom App

Create a Custom App, choose **Install via YAML**, and paste this configuration:

```yaml
services:
  truenas-update-manager:
    image: ghcr.io/amitai5/truenasautoupdater:latest
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      ASPNETCORE_URLS: http://0.0.0.0:8080
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

Open `http://<truenas-address>:8080` and complete the first-launch wizard. If port `8080` is already in use, change only the first number in `"8080:8080"`. Replace `latest` with a version such as `1.2.3` to pin the deployment.

Expose port `8080` only on the trusted network where the UI should be available. Leave privileged mode and host networking disabled, do not mount the Docker socket, and grant write access only to the persistent `/data` storage.

### Manual form settings

If the Custom App is configured with the form instead of YAML, use:

| Setting | Value |
| --- | --- |
| Internal port | `8080/tcp` |
| Persistent mount | `/data` |
| `ASPNETCORE_URLS` | `http://0.0.0.0:8080` |
| `DATA_PATH` | `/data` |
| `APP_ENCRYPTION_KEY` | Optional, base64-encoded 32-byte key |
| `TRUENAS_APP_ID` | Optional manager app ID used to block self-update |

Do not enable privileged mode, mount `/var/run/docker.sock`, use host PID, or grant filesystem access beyond `/data`. The supplied Compose configuration drops all Linux capabilities and supports a read-only root filesystem.

## TrueNAS account and URL

Create a scoped TrueNAS account/API key with the practical minimum roles:

- `APPS_READ`
- `APPS_WRITE`

Use `wss://<server>/api/current`. TLS verification is enabled by default. `ws://` and certificate bypass each require an explicit warning-backed opt-in.

Connection Test opens the WebSocket, authenticates with `auth.login_ex`, calls `core.ping`, and calls `app.query`. It reports missing app roles when the authentication response exposes them.

## First-launch behavior

No connection, schedule, timezone, policy, hostname, notification target, or notification event is preconfigured. Discovery creates every app with an **Unconfigured** policy. Unconfigured apps are visible and checked, but do not update automatically and do not generate policy-based update notifications.

The wizard covers:

1. TrueNAS connection and test
2. Optional schedule and timezone
3. Optional Email/Webhook providers and explicitly selected events
4. Read-only discovery followed by policy review

### Configure scheduled checks and updates

The schedule is saved by the manager in `/data`; it is not an environment variable or a separate TrueNAS cron task. On the wizard's **Schedule** step, enable scheduled checks and updates, enter a standard 5-field cron expression, and choose an IANA timezone such as `Etc/UTC` or `America/New_York`. You can change these values later under **Settings > Schedule**.

Cron fields are `minute hour day-of-month month day-of-week`. Seconds are not supported.

| Cron expression | Runs |
| --- | --- |
| `0 4 * * *` | Every day at 04:00 |
| `0 4 * * 0` | Every Sunday at 04:00 |
| `*/30 * * * *` | Every 30 minutes |

At each scheduled time, the manager checks installed apps and applies updates only to apps whose policies permit automatic updates. Missed runs are not replayed after a restart, and overlapping runs are skipped.

## Update and schedule safety

- Only one check/update run is active at a time. Overlapping triggers are skipped and recorded.
- Scheduled and Check & Update runs process apps sequentially.
- Check Now never executes updates.
- `action_required` always blocks unattended execution.
- RUNNING apps may auto-update. STOPPED and CRASHED apps require manual confirmation; transitional states are skipped.
- Version parsing fails closed outside Any Version scope.
- Image-only updates are not classified as semantic versions.
- Restart calculates the next future cron occurrence and does not replay missed runs.
- TrueNAS job success is followed by state/version/image verification before success is persisted.
- Persistence failure stops unattended execution before a lifecycle call.

## Notifications and retention

V1 includes exactly Email and Generic Webhook providers. HTTP `2xx` is success; webhooks retry transient network failures, `408`, `429`, and `5xx` responses with bounded backoff. Secret headers and Authorization values are never logged.

Update notification deduplication uses event type, app ID, target version or image set, and reason code. Connection failures use a configurable cooldown.

History is retained indefinitely by default. Set **History retention (days)** under Advanced / Safety to enable cleanup after scheduled runs.

## Secret encryption

Secrets are encrypted with AES-GCM before SQLite persistence and are never returned as settings values. Entering a blank secret leaves the saved value unchanged.

For stronger key separation, set `APP_ENCRYPTION_KEY` to a base64-encoded 32-byte key:

```bash
openssl rand -base64 32
```

If the variable is absent, the app creates `/data/.encryption-key` with owner-only permissions. Storing both encrypted data and its generated key in the same volume protects against casual database disclosure but does not provide the same separation as an external key.

Back up the external key with the database. Losing it makes saved secrets unrecoverable.

## Security exposure

V1 has no built-in user accounts or RBAC. Expose the UI only on a trusted LAN/VPN or behind an authenticated reverse proxy. Restrict the TrueNAS API key to the required app roles.

The UI uses ASP.NET antiforgery protection, restrictive response headers, URL scheme validation, masked secret inputs, and sanitized integration errors.

## Health endpoints

- `/health/live` — process liveness; independent of TrueNAS
- `/health/ready` — application initialization and SQLite connectivity

Temporary TrueNAS downtime does not make readiness fail.

## Development

Requires the .NET 10 SDK.

```bash
dotnet restore TrueNasUpdateManager.slnx
dotnet build TrueNasUpdateManager.slnx --no-restore
dotnet test TrueNasUpdateManager.slnx --no-build
```

The tests are hermetic and use fake transports, HTTP handlers, SMTP message construction, SQLite files under temporary directories, and deterministic `TimeProvider` values. They do not require TrueNAS, Docker, Internet, SMTP, webhooks, or wall-clock waits.

## TrueNAS API methods

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

API DTOs are intentionally narrow. Validate installed TrueNAS API schemas when upgrading across TrueNAS releases.
