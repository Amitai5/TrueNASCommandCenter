# TrueNAS Command Center

[![Production build](https://img.shields.io/github/actions/workflow/status/Amitai5/TrueNASCommandCenter/publish-container.yml?branch=production&style=for-the-badge&label=production)](https://github.com/Amitai5/TrueNASCommandCenter/actions/workflows/publish-container.yml)
[![Container image](https://img.shields.io/badge/GHCR-latest-2496ED?style=for-the-badge&logo=docker&logoColor=white)](https://github.com/Amitai5/TrueNASCommandCenter/pkgs/container/truenascommandcenter)
[![Version](https://img.shields.io/github/v/tag/Amitai5/TrueNASCommandCenter?style=for-the-badge&label=version)](https://github.com/Amitai5/TrueNASCommandCenter/tags)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![TrueNAS](https://img.shields.io/badge/TrueNAS-25.10%2B-0095D5?style=for-the-badge&logo=truenas&logoColor=white)](https://www.truenas.com/)
[![License](https://img.shields.io/badge/license-MIT-16A34A?style=for-the-badge)](LICENSE)

TrueNAS Command Center is a single-container web application for TrueNAS Community Edition / SCALE 25.10 and later. It discovers installed apps, manages their lifecycle, applies explicit per-app update policies, schedules safe checks and updates, records history, and sends optional email, webhook, or browser push notifications.

TrueNAS remains the lifecycle authority. The manager uses the TrueNAS JSON-RPC 2.0 middleware API for discovery, host information, native alerts, operating-system update status, live resource and pool status, app lifecycle actions, catalog upgrades, image refreshes, job monitoring, and rollbacks. It never controls Docker directly.

## Features

- Explicit **Auto Update**, **Notify Only**, **Ignore**, and fail-closed **Unconfigured** policies
- Conservative Any Version, Minor + Patch, and Patch-only version scopes
- Catalog upgrades and image-only refreshes
- Read-only **Discover Apps** gallery for both the native TrueNAS catalog and public Docker Hub images, with source-specific search and filters, detailed metadata, and safe installation handoffs
- Docker Hub custom-app discovery with a one-click Hot apps view, Linux-only Docker Official Image and Verified Publisher results, category and architecture filters, and copy-ready `docker.io` image references for TrueNAS
- Optional approximate active-deployment counts from TrueNAS public anonymous telemetry; catalog browsing remains available when telemetry is unavailable
- Start, stop, and restart controls backed by TrueNAS jobs
- Five-field cron scheduling with IANA timezones
- Sequential updates with TrueNAS job waiting and post-update verification
- Manual rollback to versions reported by TrueNAS
- Per-app health policies: Ignore, Notify Only, or one automatic restart attempt plus notification
- Top-level and container health, maintenance mode, recovery notifications, and lifecycle audit history
- Read-only Uptime Kuma integration with imported monitor state, response time, uptime windows, certificate status, and explicit app mapping
- At-a-glance TrueNAS server identity with resolved IP, one-click copy, and direct TrueNAS Web UI access
- Read-only System page with native TrueNAS alerts, host identity and uptime, OS update availability, and independently permissioned panels
- Read-only **Data Protection Center** with a dataset tree, snapshot coverage and newest age, replication/cloud-sync state, last success, next run, and unprotected-dataset warnings
- Read-only **Drive & Pool Health** with disk temperatures, SMART-related warnings, model/serial/capacity, pool and vdev membership, scrub/resilver progress, and ZFS error counts
- Optional storage-pool health and capacity cards when the service account has `POOL_READ`
- Operations dashboard with current app and Kuma outages, latest update-run status, schedule, server identity, storage health, and data freshness
- Durable **Operations Inbox** combining TrueNAS alerts and jobs, pool scrubs/resilvers, app update failures, Uptime Kuma outages, and notification failures with acknowledgement, resolution, filters, deep links, and deduplicated push alerts
- Live per-app CPU, memory, network, and block-I/O metrics imported from TrueNAS
- Favorites and custom app groups with dashboard filtering and portable backup support
- Prominent published ports, route-aware local/remote Web UI links, and formatted on-demand live container logs with copy and fullscreen controls
- Password-protected full-recovery JSON with validated, transactional merge restore
- Optional TrueNAS-native email, generic webhook, and per-device browser push notifications
- Optional public GitHub repository facts with 24-hour ETag caching and no token
- Encrypted API, Authorization, and secret-header values
- Detailed run, attempt, skip, failure, rollback, and notification history
- Installable Progressive Web App with desktop/mobile shortcuts and an explicit offline connection screen
- Responsive system-aware light/dark web UI with a compact mobile header, full navigation drawer, 44-pixel touch targets, mobile history cards, and safe-area support
- SQLite persistence in a dedicated `/data` volume
- Liveness and readiness endpoints

## Documentation

- **[First-Time Setup Guide](TrueNASCommandCenter/Docs/SETUP.md)** — service account, API key, connection fields, wizard steps, and troubleshooting
- **[TrueNAS API Permission Guide](TrueNASCommandCenter/Docs/PERMISSIONS.md)** — required and optional roles, recommended privilege profiles, and role-specific troubleshooting
- **[Developer Guide](TrueNASCommandCenter/Docs/DEVELOPMENT.md)** — local builds, tests, container development, architecture, publishing, and TrueNAS middleware methods
- **In-app setup help** — after installation, open `http://<truenas-address>:2600/help` or select **Help** in the web UI

## Requirements

- TrueNAS Community Edition / SCALE 25.10 or later
- A configured TrueNAS Apps storage pool
- A service account and user-linked API key with `APPS_READ` and `APPS_WRITE`; add `CATALOG_READ` for Discover. Optional read-only centers name their required roles inline: Data Protection uses `DATASET_READ`, `SNAPSHOT_READ`, `SNAPSHOT_TASK_READ`, `REPLICATION_TASK_READ`, and `CLOUD_SYNC_READ`; Drive Health uses `POOL_READ`, `DISK_READ`, `REPORTING_READ`, and `ALERT_LIST_READ`.
- A trusted LAN/VPN, or an authenticated reverse proxy in front of the web UI

The application does not include its own user accounts or RBAC. Do not expose it directly to an untrusted network.

`CATALOG_READ` is not included by `APPS_READ`. After adding or changing a role, run **Settings → Connection → Test connection** or use **Discover → Reconnect & retry** so the WebSocket authenticates with the updated privilege. See the [permission guide](TrueNASCommandCenter/Docs/PERMISSIONS.md) for least-privilege profiles and feature-by-feature behavior.

## Installation

### TrueNAS Custom App via YAML

This is the recommended TrueNAS installation method.

1. Open **Apps → Discover**.
2. Open the menu beside **Custom App** and select **Install via YAML**.
3. Enter an application name such as `truenas-command-center`.
4. Paste the following Compose configuration into **Custom Config**.
5. Click **Save** and wait for the app to report a running state.

```yaml
services:
  truenas-command-center:
    cap_drop:
      - ALL
    environment:
      ASPNETCORE_HTTP_PORTS: 2600
      DATA_PATH: /data
      TRUENAS_APP_ID: truenas-command-center
      TRUENAS_WEBSOCKET_URL: wss://truenas.local/api/current
    extra_hosts:
      - truenas.local:10.0.0.21
    image: ghcr.io/amitai5/truenascommandcenter:latest
    labels:
      org.opencontainers.image.description: Manage, monitor, inspect, and safely update TrueNAS apps.
      org.opencontainers.image.source: https://github.com/Amitai5/TrueNASCommandCenter
      org.opencontainers.image.title: TrueNAS Command Center
      org.opencontainers.image.url: https://github.com/Amitai5/TrueNASCommandCenter
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
  TrueNAS Command Center monitors app health, exposes ports and Web UI links, streams container logs,
  and refreshes inventory before every scheduled update check. Open the Web UI to finish setup.
x-portals:
  - host: 0.0.0.0
    name: Web UI
    path: /
    port: 2600
    scheme: http
```

Open `http://<truenas-address>:2600`. Custom apps installed from YAML might not receive a **Web UI** button in TrueNAS, so navigate to the address directly.

### Install on a phone, tablet, or desktop

Open the manager through an HTTPS address, then choose **Install app** in the desktop sidebar or mobile header. Chrome and Edge show the native install prompt. Samsung Internet may instead show its install icon in the address bar; if it does not, open the Samsung Internet menu and choose **Add page to → Home screen**, then confirm **Install on Apps screen**. The Command Center now shows browser-specific instructions and live checks for HTTPS, the app manifest, and the service worker whenever a browser does not expose its native prompt. On iPhone and iPad, open the browser Share menu and choose **Add to Home Screen**. The installed app opens in its own window and includes shortcuts to Dashboard, Inbox, Apps, and Monitoring.

Browser security rules do not permit manifest-based installation from plain `http://truenas.local` or a private IP, including on Samsung Galaxy devices. Use an authenticated HTTPS reverse proxy for an installable production address; `http://localhost` and `http://127.0.0.1` remain valid for local development. Also open the address in a full browser rather than an embedded browser inside another app. The service worker caches only the branded offline screen and static icon assets. Live TrueNAS status, logs, Uptime Kuma reports, and management actions always require a working connection to the Command Center server.

### Enable browser push notifications

Open **Settings → Notifications → Browser push** from each phone, tablet, or computer that should receive alerts, give the device an optional name, and select **Enable on this device**. Permission is requested only from that explicit click. Use **Send test push** before relying on the device, and use **Forget** to retire a device you no longer control.

Push requires the same secure context as PWA installation. On iPhone and iPad, first add the Command Center to the Home Screen and enable push from the installed app. The Command Center container must also be able to make outbound HTTPS requests to the push-service host returned by each browser. Each browser subscription is stored locally in `/data`; the VAPID private key is encrypted at rest. Browser-vendor push services receive an authenticated, payload-free wake-up—not app names, TrueNAS addresses, or error details. The device displays a generic alert and opens the Operations Inbox for details. Push is sent for attention events such as app downtime, failed recovery, manual approval, blocked or failed updates, rollback, scheduled-check failure, TrueNAS connection failure, and new warning-or-higher Operations Inbox incidents. Per-app downtime delivery still follows that app's configured downtime action. Notification-delivery failures appear in the inbox but do not trigger another push, preventing a recursive failure loop.

This configuration uses the current TrueNAS Web UI address, `10.0.0.21`. If that address changes, update the complete YAML's `extra_hosts` value before redeploying. Prefer a DHCP reservation or static address. If your certificate uses a different hostname, replace `truenas.local` in both `extra_hosts` and `TRUENAS_WEBSOCKET_URL`.

The Dashboard resolves that configured hostname and displays its current IP in the server status strip. Desktop layouts repeat the address in the sidebar so it remains available while navigating the manager. The status strip can copy the IP or open the local TrueNAS Web UI over `http://` without storing a second server address.

Host networking is the reliable default because it lets the manager reach the TrueNAS Web UI address without Docker bridge or LAN hairpin failures. Host mode does not use Docker port publishing; the ASP.NET listener binds directly to the host network.

`ASPNETCORE_HTTP_PORTS` controls the listener, while `x-app-port` records the same port for the TrueNAS configuration. If `2600` is already in use, change both values to the same unused port above `1023`, save the complete YAML, and open that port in the browser.

### Optional bridge-network deployment

Bridge networking makes port `2600` appear in TrueNAS workload metadata, but some TrueNAS hosts cannot route a custom-app bridge back to their own Web UI address. Use this complete alternative only when **Test connection** succeeds; otherwise return to the host-network YAML above.

```yaml
services:
  truenas-command-center:
    cap_drop:
      - ALL
    environment:
      ASPNETCORE_HTTP_PORTS: 2600
      DATA_PATH: /data
      TRUENAS_APP_ID: truenas-command-center
      TRUENAS_WEBSOCKET_URL: wss://truenas.local/api/current
    extra_hosts:
      - truenas.local:10.0.0.21
    image: ghcr.io/amitai5/truenascommandcenter:latest
    labels:
      org.opencontainers.image.description: Manage, monitor, inspect, and safely update TrueNAS apps.
      org.opencontainers.image.source: https://github.com/Amitai5/TrueNASCommandCenter
      org.opencontainers.image.title: TrueNAS Command Center
      org.opencontainers.image.url: https://github.com/Amitai5/TrueNASCommandCenter
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
  TrueNAS Command Center monitors app health, exposes ports and Web UI links, streams container logs,
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
  --name truenas-command-center \
  --restart unless-stopped \
  --network host \
  --add-host truenas.local:10.0.0.21 \
  --env TRUENAS_APP_ID=truenas-command-center \
  --env TRUENAS_WEBSOCKET_URL=wss://truenas.local/api/current \
  --mount source=update-manager-data,target=/data \
  --read-only \
  --tmpfs /tmp:size=64m,mode=1777 \
  --cap-drop ALL \
  --security-opt no-new-privileges=true \
  ghcr.io/amitai5/truenascommandcenter:latest
```

If the TrueNAS Web UI address changes from `10.0.0.21`, update `--add-host`, then open `http://localhost:2600` and follow the [First-Time Setup Guide](TrueNASCommandCenter/Docs/SETUP.md).

## First launch

The wizard uses the secure TrueNAS endpoint configured in the deployment YAML but does not preconfigure credentials, schedule, timezone, policy, notification target, or notification event.

1. Enter a dedicated TrueNAS service account and test its API key. Keep certificate verification enabled when the certificate is trusted and covers the hostname in `TRUENAS_WEBSOCKET_URL`.
2. Optionally configure scheduled checks and updates.
3. Optionally configure TrueNAS-native email or webhook notifications.
4. Discover installed apps and assign an explicit policy to each one.

The **Continue** button on the connection step remains disabled until **Test connection** succeeds. See the [setup guide](TrueNASCommandCenter/Docs/SETUP.md) or the in-app **Help** page for account, certificate, connection, and browser troubleshooting.

## Dashboard, system health, and app organization

The Dashboard keeps server-wide operational information separate from the app inventory. It surfaces current app and Uptime Kuma outages, the latest check/update result, the next scheduled run, TrueNAS identity and IP, storage-pool status, and freshness timestamps. Operator-facing timestamps use a 12-hour clock with AM/PM.

Seven optional or automatic views become available after the initial connection succeeds:

1. **TrueNAS IP and Web UI actions** — set `TRUENAS_WEBSOCKET_URL` to the certificate-covered TrueNAS hostname and map that hostname to the current TrueNAS IP with `extra_hosts` in the complete YAML. The manager resolves the hostname automatically, shows the address in the server status strip and desktop sidebar, and enables **Copy IP** and **Open TrueNAS**. There is no second IP setting to maintain.
2. **Storage-pool health** — edit the custom privilege assigned to the service-account group and add the optional `POOL_READ` role. Return to the Dashboard and select **Refresh pools**. Without `POOL_READ`, app management continues normally and only the pool cards remain unavailable.
3. **Native TrueNAS system health** — add `ALERT_LIST_READ` for active/dismissed alerts and `SYSTEM_UPDATE_READ` for OS update availability. The broader read-only `READONLY_ADMIN` role additionally enables hostname, version, hardware, load, boot time, and uptime. Open **System**; missing roles affect only their own panel, and the page never dismisses alerts or installs OS updates.
4. **Live app resources** — `APPS_READ`, already required for discovery, also permits the shared `app.stats` stream. No additional setting is needed. After a successful connection, CPU and memory appear on the Apps page after the first sample; open an app's details page for network and block-I/O values. Samples remain in memory and are never added to history or backups.
5. **Favorites and groups** — select the star beside an app to favorite it. Open **App settings → Organization** to assign a group such as `Media`, `Infrastructure`, or `Home automation`, then use the Apps-page filter to show favorites, one group, or ungrouped apps. Favorites and groups are included in the password-protected full recovery backup.
6. **Data Protection Center** — add `DATASET_READ` and `SNAPSHOT_READ` for the dataset tree and newest snapshot age; add `SNAPSHOT_TASK_READ`, `REPLICATION_TASK_READ`, and `CLOUD_SYNC_READ` for coverage, state, last-success, and next-run details. The page marks user datasets with no enabled snapshot, outbound replication, or outbound cloud-sync path as unprotected. Every source remains independent and read-only.
7. **Drive & Pool Health** — add `POOL_READ` for topology, error counters, and scrub/resilver progress; `DISK_READ` for drive identity; `REPORTING_READ` for cached temperatures and critical thresholds; and `ALERT_LIST_READ` for active SMART/storage warnings. Missing roles affect only their source card.

The **Operations Inbox** refreshes automatically every minute and can also be refreshed on demand. It combines native TrueNAS alerts (`ALERT_LIST_READ`), pool scrub/resilver activity (`POOL_READ`), recent TrueNAS jobs visible to the authenticated account, local app-update and notification failures, and imported Uptime Kuma outages. Scoped accounts can see jobs owned by their current API session; TrueNAS exposes jobs from other sessions only to a Full Admin account. Full Admin is optional and broader than the recommended least-privilege profile, so grant it only when cross-session job visibility is worth that access.

See [Manage, monitor, and inspect apps](TrueNASCommandCenter/Docs/SETUP.md#6-manage-monitor-and-inspect-apps) for detailed TrueNAS navigation, validation steps, and troubleshooting.

## App access, logs, and configuration backups

Each app policy has separate **Local Web UI URL** and **Remote Web UI URL** fields. When the manager is opened through `truenas.local`, localhost, or a private/link-local address, its Web UI buttons use the local route. When it is opened through a public domain such as `apps.example.com`, the buttons use the explicitly configured remote route. Remote addresses are never guessed. Generated local links default to `http://truenas.local` instead of an IP address; the global **Local TrueNAS Web UI host** setting can override that origin.

The app-details page prioritizes operations. A large live-log workspace sits beside a bounded access-and-workloads column, followed by full-width overview cards for application metadata, Uptime Kuma, updates and recovery, safety, and recent history. This shared page flow keeps secondary cards from extending beside empty content. On smaller screens, every section stacks into one column without horizontal overflow.

The primary navigation is ordered **Dashboard**, **Inbox**, **Apps**, **Discover**, **System**, **Data protection**, **Drive health**, **Monitoring**, **History**, and **Settings**. The system-aware light and dark themes use the same information hierarchy, with a persistent manual toggle and stronger light-theme borders, text contrast, inputs, badges, and active navigation states.

The Apps page can star frequently used apps, assign a custom group under each app's **Settings**, and filter by favorites, group, or ungrouped apps. It also shows current CPU and memory at a glance. The details page adds live CPU, memory, network, and block-I/O metrics alongside the current route, ports, health, workloads, versions, lifecycle controls, source information, and live logs. Resource values come from TrueNAS's shared statistics stream and are not persisted.

The Dashboard and System page show pool status, used/free capacity, and fragmentation when `POOL_READ` is present. App management continues normally when any optional system-read role is absent; each System panel explains the exact role it needs. Logs contain at most the latest 500 loaded lines, stay in browser memory, and can be selected manually, copied as ISO-8601 text, or opened fullscreen. A successfully completed `permissions` helper workload is shown as **Exited normally** and does not degrade an otherwise running app.

## Uptime Kuma reports

Open **Settings → Uptime Kuma** to connect the manager to an existing Uptime Kuma server. Configure the server-to-server connection URL, an optional browser URL, and a Prometheus API key created under **Uptime Kuma → Settings → Security → API Keys**. The connection URL can be a LAN address such as `http://truenas.local:3001`; the browser URL can be a separately published address such as `https://status.example.com`.

The manager reads only Uptime Kuma's `/metrics` endpoint. It imports current monitor status, response time, 1-day/30-day/365-day uptime ratios, 30-day average response time, and certificate validity/expiry when Kuma publishes those metrics. Prometheus API keys require Uptime Kuma 1.21 or later; detailed uptime-window metrics require Uptime Kuma 2.x and appear as unavailable on older releases. The manager does not create probes, change monitors, import incident history, or duplicate Kuma notifications. Use an app's **Settings** page to map one or more imported monitors, then view the consolidated report under **Monitoring** and the selected app's details page. Saving a connection URL starts automatic imports at the configured interval; clearing the connection URL disconnects Kuma. Manual **Sync now** remains available for immediate refreshes.

The API key is stored encrypted. Keep TLS verification enabled for HTTPS connections whenever the certificate is trusted. A failed refresh leaves the last successful report visible and marks it stale instead of replacing it with an artificial outage.

Open **Settings → Backup & restore** for portable configuration backups:

- **Full recovery JSON** is the disaster-recovery option. It contains every saved global setting and per-app configuration, including the TrueNAS API key, Uptime Kuma API key, webhook authorization and secret headers, browser push VAPID identity and device subscriptions, schedules, notifications, monitor mappings, policies, favorites, groups, downtime behavior, maintenance state, and local/remote Web UI links. The payload is protected with a password-derived key and can rebuild a fresh installation without the previous `/data` volume.
- Restore accepts only password-protected full recovery JSON files. Secret-free exports are not accepted, and the password is required for both validation and import.
- Imports validate and authenticate the complete file before a transactional merge. Listed app configurations are restored by app ID, unlisted apps and existing history remain unchanged, and undiscovered app policies are held until the next inventory refresh.

After a full recovery import, the manager resets its TrueNAS client and refreshes inventory when the restored credentials are usable. TrueNAS inventory, Uptime Kuma reports, and other live data are regenerated from their source systems. Logs, resource samples, health incidents, GitHub cache, notification deliveries, and update history remain intentionally excluded; back up `/data` as well only when you need that historical record. Store the recovery password separately because it cannot be derived from the JSON.

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

Every production release has one semantic version stored in [`VERSION`](TrueNASCommandCenter/VERSION). The running version appears in the sidebar, on the Settings page, in startup logs, in the `X-Application-Version` response header, and at `/version`. The container carries the same `org.opencontainers.image.version` label.

The `latest` and `production` image tags track the current `production` branch. Immutable release tags such as `1.1.0`, minor-channel tags such as `1.1`, and commit tags such as `sha-<commit>` are published together. Increment `VERSION` before the next production release; publishing refuses to reuse a version that already belongs to another commit. Pin the image to an exact version when reproducibility matters, or keep `latest` with `pull_policy: always` for automatic image discovery.

In **Apps → Configuration → Settings**, keep **Check for docker image updates** enabled. To apply an available image, update/redeploy the custom app or edit its YAML and save without changing the `/data` volume. `pull_policy: always` does not restart a running container by itself; it takes effect when TrueNAS reapplies the Compose project. After an update, hard-refresh the browser if it has cached older frontend assets.

### Upgrading from TrueNAS App Manager

Version 2.0.0 renames the product, repository, projects, image, and PWA to **TrueNAS Command Center**. Existing `/data` volumes and password-protected recovery JSON files remain compatible. The production workflow also publishes the legacy `ghcr.io/amitai5/truenasappmanager` image alias so existing deployments continue receiving updates while they move to `ghcr.io/amitai5/truenascommandcenter`.

Keep `TRUENAS_APP_ID=truenas-app-manager` when that is still the installed app ID shown by TrueNAS; change it only if the TrueNAS app itself is recreated or renamed to `truenas-command-center`.

## Health endpoints

- `/health/live` — process liveness, independent of TrueNAS connectivity
- `/health/ready` — application initialization and SQLite connectivity

Temporary TrueNAS downtime does not make application readiness fail.

## TrueNAS custom-app metadata limits

The YAML supplies a Web UI portal, operator notes, and OCI labels. TrueNAS still identifies YAML installs as custom apps, so its native **Application Info** card can continue to show a generic icon, `App Version: custom`, and `Source: N/A`. The published image's `org.opencontainers.image.version` label and the Command Center's persistent running-version display provide the authoritative release number. TrueNAS Command Center displays richer workload data and optional GitHub facts inside its own app-details page; it does not create unsupported catalog metadata files or catalog routes.
