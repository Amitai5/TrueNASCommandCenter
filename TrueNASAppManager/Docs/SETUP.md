# First-Time Setup Guide

[Back to the main README](../../README.md) · [Developer guide](DEVELOPMENT.md)

This guide walks through installing TrueNAS App Manager, creating a least-privilege TrueNAS service account, connecting the first-launch wizard, and resolving the most common setup problems. The TrueNAS navigation names below follow Community Edition / SCALE 25.10 and later.

## Before you begin

You need:

- TrueNAS Community Edition / SCALE 25.10 or later with the Apps service running.
- An administrator account that can create users, groups, privileges, and API keys.
- Network access from your browser to port `2600` on the TrueNAS system, or another port configured through `x-app-port` in the YAML.
- The current TrueNAS Web UI IPv4 address and the hostname covered by its TLS certificate.
- A trusted LAN or VPN. The manager does not include its own user login or RBAC.

Install the container first using the [TrueNAS Custom App or Docker instructions](../../README.md#installation). After installation, open:

```text
http://<truenas-address>:2600
```

The liveness endpoint should return a successful response at `http://<truenas-address>:2600/health/live`.

### Optional Progressive Web App installation

For an app-like launcher on a phone, tablet, or desktop, open TrueNAS App Manager through an authenticated HTTPS reverse proxy and select **Install app** from the sidebar or mobile header. Chromium browsers normally display their native installation prompt. On Samsung Internet, use the address-bar install icon or choose **Add page to → Home screen** from the browser menu and confirm **Install on Apps screen**. When no native prompt is available, the App Manager opens browser-specific instructions plus checks for HTTPS, a valid manifest, and an active service worker. On iPhone and iPad, use the browser Share menu and select **Add to Home Screen**.

Plain `http://truenas.local` and private-IP URLs remain supported for normal browser use, but Android and desktop browsers do not consider them eligible for PWA installation. Only HTTPS, or `http://localhost` / `http://127.0.0.1` during development, meets the secure installation requirement. If the page was opened inside Google Search, Gmail, Facebook, Instagram, or another embedded browser, open it in Samsung Internet or Chrome before installing. The installed shell shows a purpose-built offline screen when the manager cannot be reached; app state, logs, monitoring, and lifecycle actions are intentionally not cached and still require a live server connection.

## 1. Create a service account and API key

Use a dedicated account instead of an administrator's personal API key. API keys provide password-equivalent middleware access and should be stored securely.

### Create the account

1. In TrueNAS, open **Credentials → Users**.
2. Click **Add**.
3. Enter a descriptive full name and a username such as `autoupdate`.
4. Use a strong random password and keep **Shell Access** and **SSH Access** disabled.
5. Create or select a dedicated primary group for the account, then save it.

The username is case-sensitive. You will enter this exact username in TrueNAS App Manager.

### Grant the required app roles

1. Open **Credentials → Groups**.
2. Click **Privileges**, then **Add**.
3. Name the privilege something descriptive, such as `TrueNAS App Manager`.
4. Under **Local Groups**, select the service account's primary group.
5. Under **Roles**, select:
   - `APPS_READ`
   - `APPS_WRITE`
   - Optionally, `POOL_READ` for storage-pool health and capacity
   - Optionally, `ALERT_LIST_READ` for native TrueNAS alerts
   - Optionally, `SYSTEM_UPDATE_READ` for TrueNAS operating-system update availability
   - Optionally, `READONLY_ADMIN` for host identity, hardware, load, and uptime
6. Leave **Web Shell Access** disabled and save the privilege.

`APPS_READ` allows discovery, health, ports, portals, containers, logs, and live application resource statistics. `APPS_WRITE` allows starts, stops, restarts, upgrades, image refreshes, and rollbacks. The four System-page permissions are not required for app management, and every unavailable read-only panel explains the exact role it needs. `READONLY_ADMIN` is intentionally broader than the three focused read roles; omit it if host hardware details are not worth that additional visibility. TrueNAS systems with a STIG security profile do not permit write roles; those systems cannot perform lifecycle or app-update actions through this account.

### Create the API key

1. Return to **Credentials → Users**.
2. Select the service account.
3. Click **Add API Key**, or open **View API Keys** and then click **Add API Key**.
4. Enter a descriptive key name and choose an expiration policy.
5. Save the key and copy it immediately.

TrueNAS displays the complete key only when it is created or reset. If it is lost, reset the key and update the saved key in the manager under **Settings → TrueNAS connection**.

## 2. Connect the first-launch wizard

Open TrueNAS App Manager and complete **Step 1: TrueNAS connection**.

The application connects through the `TRUENAS_WEBSOCKET_URL` configured in the custom app YAML. There is no browser-editable server URL field or insecure `ws://` mode.

Before opening the wizard, replace the entire TrueNAS custom app YAML with this complete configuration:

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

The YAML uses the current TrueNAS Web UI address, `10.0.0.21`. If that address changes, update the `extra_hosts` value in the complete YAML. If the TrueNAS certificate uses a different hostname, replace `truenas.local` in both `extra_hosts` and `TRUENAS_WEBSOCKET_URL`. Prefer a DHCP reservation or static address so the mapping does not become stale.

After startup, the Apps status strip displays the IP resolved from `TRUENAS_WEBSOCKET_URL`; desktop layouts also show it in the sidebar. An **IP unavailable** value means the container could not resolve the configured hostname and the `extra_hosts` mapping should be checked.

| Field | Recommended value |
| --- | --- |
| Username | The API key owner's exact username, such as `autoupdate` |
| API key | The key copied from TrueNAS |
| Verify TLS certificate | Enabled when the certificate is trusted and covers the hostname in `TRUENAS_WEBSOCKET_URL` |

The documented deployment uses TrueNAS Host Network mode and an explicit hostname mapping. This avoids Docker bridge routing and unreliable `.local` name resolution while preserving TLS hostname validation. Previously saved server URLs and insecure-WebSocket settings are ignored after upgrading and normalized to the deployment endpoint the next time settings are saved.

TrueNAS requires secure transport for user-linked API keys. The manager enforces `wss://` and does not expose an insecure transport option.

### Optional bridge-network YAML

Bridge mode lets TrueNAS display port `2600` in native workload metadata. Some TrueNAS hosts cannot route a custom-app bridge back to their own Web UI address, so use this complete configuration only when **Test connection** succeeds. If it fails with routing or connection errors, restore the complete host-network YAML above.

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

Click **Test connection**. A successful test verifies authentication, pings middleware, checks app access, and enables **Continue**. The Continue button intentionally remains disabled until the connection test succeeds.

If TrueNAS uses an untrusted or self-signed certificate, the preferred fix is to configure a trusted certificate that covers the configured hostname. On a trusted LAN, disabling certificate verification can be used as a temporary workaround while keeping `wss://` encryption enabled.

## 3. Configure the schedule

Scheduled checks are optional. If enabled, enter a standard five-field cron expression and an IANA timezone such as `Etc/UTC` or `America/Los_Angeles`.

| Cron expression | Runs |
| --- | --- |
| `0 4 * * *` | Every day at 04:00 |
| `0 4 * * 0` | Every Sunday at 04:00 |
| `*/30 * * * *` | Every 30 minutes |

The schedule is stored in `/data`; no separate TrueNAS cron task is needed. Missed runs are not replayed after a restart, and overlapping runs are skipped.

## 4. Configure notifications

Email, generic webhook, and browser push notifications are optional. Enable only the providers and event types you want.

- Email uses the existing TrueNAS mail configuration through the authenticated `mail.send` method. Leave recipients blank to use TrueNAS administrator addresses, or enter explicit recipients.
- Webhooks require an HTTPS endpoint and can include an Authorization value or secret headers.
- Browser push is enabled separately on each device under **Browser push**. Enter an optional device name, select **Enable on this device**, approve the browser prompt, and send a test push. Use **Forget** for devices that should no longer receive alerts.
- Use the test button for each configured provider before continuing.

Secrets are encrypted before being stored. Leaving a saved secret field blank preserves the existing value.

Browser push requires an HTTPS App Manager address; only `localhost` and `127.0.0.1` receive the browser's HTTP development exception. Plain `http://truenas.local` and private-IP pages cannot request notification permission. The App Manager container also needs outbound HTTPS access to the push-service host contained in each browser subscription. On iPhone and iPad, add the PWA to the Home Screen first and enable notifications from the installed app. Push notifications intentionally contain no app name, server address, or error text while traversing a browser-vendor push service. They open the local Dashboard for details. Push covers attention events only; app-down alerts also require that app's downtime action to be **Notify Only** or **Restart and Notify**.

## 5. Discover apps and assign policies

The final wizard step performs read-only discovery. It does not update applications.

Every discovered app starts with an **Unconfigured** policy. Review each app and choose one of:

- **Auto Update** — apply updates automatically within the selected version scope.
- **Notify Only** — report an available update without installing it.
- **Ignore** — keep the app visible but take no update action.

This fail-closed default prevents newly discovered applications from updating without an explicit policy.

## 6. Manage, monitor, and inspect apps

Open an app from the **Apps** page to start, stop, or restart it through TrueNAS. The app list also provides a quick **Start** action for stopped or crashed apps and a **Restart** action for running apps. Lifecycle actions wait for the TrueNAS job to finish before refreshing the displayed state.

### Configure the TrueNAS address actions

The address display is automatic and does not require another saved setting:

1. Confirm `TRUENAS_WEBSOCKET_URL` uses the TrueNAS hostname covered by its TLS certificate, such as `wss://truenas.local/api/current`.
2. Confirm the complete custom-app YAML maps that same hostname to the current TrueNAS IP under `extra_hosts`, such as `truenas.local:10.0.0.21`.
3. Redeploy the custom app after changing either value.
4. Open the Dashboard and confirm the server status strip shows the expected hostname and IP. Desktop layouts also repeat the IP in the sidebar.
5. Use **Copy IP** to place the numeric address on the clipboard or **Open TrueNAS** to open the local Web UI over `http://`.

If the address shows **IP unavailable**, correct the `extra_hosts` mapping and redeploy the complete YAML. Prefer a DHCP reservation or static address so the mapping remains valid.

### Enable storage-pool health

Pool cards require the additional read-only `POOL_READ` role:

1. In TrueNAS, open **Credentials → Groups → Privileges**.
2. Edit the custom privilege assigned to the App Manager service account's group.
3. Add `POOL_READ` beside the existing `APPS_READ` and `APPS_WRITE` roles, then save.
4. Return to TrueNAS App Manager and select **Refresh pools** on the Dashboard. If the API session was already open when the role changed, run **Settings → TrueNAS connection → Test connection** once before refreshing.
5. Confirm each pool card shows its TrueNAS health state, used/free capacity, and fragmentation value.

`POOL_READ` is optional. A missing or denied role displays an explanatory unavailable state and never blocks app discovery, monitoring, or lifecycle operations.

### Enable the read-only System overview

The **System** page combines independent TrueNAS capabilities. Add only the read roles you want:

1. Add `ALERT_LIST_READ` for active and dismissed native TrueNAS alerts.
2. Add `SYSTEM_UPDATE_READ` for the configured update train, profile, download progress, and available OS version. Installing the update remains in the TrueNAS Web UI.
3. Add `POOL_READ` for pool health, capacity, and fragmentation.
4. Optionally add the broader `READONLY_ADMIN` role for hostname, TrueNAS version, CPU, memory, load average, boot time, and uptime.
5. Save the privilege, run **Settings → TrueNAS connection → Test connection** once to reopen the API session, then open **System** and select **Refresh system**.

The page never dismisses alerts, installs operating-system updates, changes pool state, or exposes the system serial number. One denied capability does not hide the other panels.

### Enable live resource statistics

Live CPU, memory, network, and block-I/O data uses the required `APPS_READ` role and needs no separate switch:

1. Confirm the service-account privilege includes `APPS_READ` and the connection test succeeds.
2. Open the Apps page and wait for the first TrueNAS statistics sample. CPU and memory appear in the resource column when TrueNAS reports them.
3. Open **Details** for an app to see its current CPU, memory, network receive/transmit, and block read/write values.

One shared server-side subscription supplies all app cards. Samples are kept only in memory, reset when the manager restarts, and are intentionally excluded from history and configuration backups. Apps for which TrueNAS has not yet published a sample display a waiting or unavailable value rather than a fabricated zero.

### Configure favorites and groups

Favorites and groups are local App Manager organization settings and require no additional TrueNAS role:

1. Select the star beside any app to add or remove it from favorites.
2. Open the app's **Settings** page and find **Organization**.
3. Optionally enable **Favorite**, enter a group name such as `Media`, `Infrastructure`, or `Home automation`, and select **Save & return**.
4. Use the Apps-page organization filter to show all apps, favorites, ungrouped apps, or one named group. Favorites sort ahead of other apps within the selected view.
5. Use **Settings → Backup & restore → Download full recovery JSON** to preserve these values and their credentials for a future reinstall.

Group names are limited to 64 characters. Restoring an older supported backup leaves the destination's existing favorites and group assignments unchanged; schema version 3 and later exports include both fields. Schema version 4 also preserves completed setup state for a seamless fresh-install restore.

To monitor an app, open its **Settings** page and choose **Notify only** or **Restart once and notify** under **When this app is down**. Health checks include the top-level app state and reported containers. Each incident sends one downtime event; recovery sends a separate event. Automatic recovery is attempted at most once per incident. Stops initiated from this manager enter maintenance mode and do not alert. A completed `permissions` initialization workload is neutral and appears as **Exited normally** rather than degrading a running app.

The app-details page uses an operations-first layout. Live logs occupy the main workspace, while published ports, local and remote Web UI links, and workloads stay in a compact adjacent column. Application metadata, Uptime Kuma reports, update and rollback information, safety state, and recent history use the full page width below that workspace instead of continuing down a narrow sidebar. On mobile, these sections stack into one column without horizontal scrolling.

At tablet and phone widths, the desktop sidebar becomes a compact sticky header and a full navigation drawer so every destination retains a readable touch target. Dense app and monitor tables already use mobile cards, and History changes to summary cards instead of requiring a wide horizontal table. Safe-area padding keeps controls clear of notches and home indicators.

Logs are bounded to 500 lines in browser memory and are never persisted. Use **Copy all** for ISO-8601 plain text or **Fullscreen** for a focused console. Optional GitHub enrichment is disabled by default and only queries canonical public `github.com` sources.

The sidebar order is **Dashboard**, **Apps**, **System**, **Monitoring**, **History**, and **Settings**. The Dashboard contains server-wide status, current app and Kuma alerts, favorite apps, the latest update run, the next scheduled run, pool health, and data-freshness timestamps. **System** contains native TrueNAS alerts, OS update availability, host details, and pool health. The Apps page remains focused on app inventory and app-specific actions. All operator-facing timestamps use a 12-hour clock with AM/PM. Use the persistent **Theme** control at the bottom of the sidebar—or in the mobile navigation—to switch between the higher-contrast light theme and dark theme.

Configure separate **Local Web UI URL** and **Remote Web UI URL** values under the app's **Settings** page when it is available through different addresses. Local manager hosts such as `truenas.local`, localhost, and private IP addresses use the local route. Generated local links default to `http://truenas.local`; the global **Local TrueNAS Web UI host** setting can override that origin. Public manager domains use only the explicitly configured remote route, and the manager does not guess subdomains.

## 7. Back up and restore configuration

Open **Settings → Backup & restore** to create a portable export:

- **Download full recovery JSON** backs up every saved global setting and per-app configuration, including the TrueNAS and Uptime Kuma API keys, webhook authorization and secret headers, browser push identity and device subscriptions, schedules, notifications, app-to-monitor mappings, policies, favorites and groups, downtime behavior, maintenance settings, and local/remote Web UI URLs. Enter and confirm a password of at least 12 characters; the JSON payload is encrypted and the password cannot be recovered.

To restore, install and open a fresh TrueNAS App Manager instance, select a password-protected full recovery JSON file up to 2 MB, enter its password, select **Validate & preview**, review the number of app configurations, and confirm the import. Secret-free backup files are rejected. Restore is a transactional merge by app ID: unlisted apps and existing history remain unchanged. Settings for apps not yet discovered are retained and applied when the next inventory refresh finds them. Restore re-encrypts imported secrets with the new installation's local key, resets the TrueNAS client, and refreshes inventory when the restored credentials are usable.

TrueNAS inventory and Uptime Kuma reports are regenerated from their source systems after a full restore. Portable JSON does not contain logs, live resource samples, health incidents, GitHub cache, notification deliveries, or update history because none of those are required to recreate the manager configuration. Back up the persistent `/data` volume as well only when you need that operational history. Store the recovery password separately from the JSON.

## 8. Connect Uptime Kuma

This integration is optional and read-only. Uptime Kuma remains responsible for probes, checks, alerts, and incident history; TrueNAS App Manager imports its current report instead of recreating those features.

1. In Uptime Kuma, open **Settings → Security → API Keys** and create a Prometheus API key.
2. In TrueNAS App Manager, open **Settings → Uptime Kuma**.
3. Enter the **Connection URL** reachable from the App Manager container, for example `http://truenas.local:3001`.
4. Optionally enter a separate **Browser URL**, for example `https://status.example.com`. Open links use this address, while background synchronization continues to use the connection URL.
5. Enter the Uptime Kuma API key, keep TLS verification enabled for a trusted HTTPS certificate, choose a refresh interval, and select **Test connection**.
6. Select **Sync now**, then open an app's **Settings** page to map one or more imported monitors.
7. Open **Monitoring** for the consolidated report or the app's details page for its mapped monitor status.

The manager reads `/metrics` using HTTP Basic authentication with an empty username and the API key as the password. Prometheus API keys require Uptime Kuma 1.21 or later. Detailed uptime-window and average-response metrics require Uptime Kuma 2.x; older releases still provide current status, response, and certificate metrics, while unavailable values display as a dash. It never stores Kuma administrator credentials or writes to Kuma.

If a sync fails, confirm the connection address is reachable from the container, the API key is active, and the certificate is valid for the configured hostname. The last successful report remains cached and is labeled stale until synchronization succeeds again.

Saving a connection URL starts scheduled imports automatically. Clear the connection URL to disconnect Kuma; the last imported report remains cached, and **Sync now** remains available whenever a connection is configured.

## Troubleshooting

### Test connection or Continue does nothing

1. Hard-refresh the browser with `Ctrl+Shift+R` or clear the site cache.
2. Confirm the running container uses the newest image, then redeploy it if necessary.
3. In browser developer tools, verify `/_framework/blazor.web.js` returns HTTP `200` as JavaScript.

The page can render as static HTML even when the Blazor bootstrap script fails. In that state, form fields appear normally but button handlers are not connected.

### TrueNAS keeps using an older image

1. Edit the custom app and replace its configuration with the complete host-network YAML in this guide; it already includes `pull_policy: always`.
2. Open **Apps → Configuration → Settings** and enable **Check for docker image updates**.
3. Save/redeploy the custom app while preserving the existing `/data` volume.

`pull_policy: always` tells Compose to check GHCR whenever TrueNAS applies or recreates the app. It does not periodically restart a running container. If TrueNAS still has not detected the new digest, use **Apps → Configuration → Manage Container Images → Pull Image**, pull `ghcr.io/amitai5/truenasappmanager:latest`, and then save/redeploy the custom app.

Confirm the deployed release number in the App Manager sidebar or Settings header. The same value is available at `/version`, in the container startup logs, and in the image's `org.opencontainers.image.version` label. TrueNAS may continue to label a YAML custom app as `custom`; that native label is not the application release number.

### Continue is disabled

This is expected until **Test connection** completes successfully. Read the status message above the form and correct the reported connection, authentication, certificate, or role error.

### Certificate validation failed

- Configure a trusted TrueNAS certificate that covers the hostname in `TRUENAS_WEBSOCKET_URL` when possible.
- Confirm the `extra_hosts` hostname exactly matches that certificate name.
- For a temporary trusted-LAN workaround, disable only certificate verification; the connection remains encrypted with `wss://`.

### Authentication failed

- Confirm the username is the account that owns the API key.
- API keys are shown only once; reset the key if the saved value is uncertain.

### Collect connection diagnostics

Every failed **Test connection** now displays an error code and diagnostic ID. Open the TrueNAS app's container logs and search for that diagnostic ID. The matching entries show the connection stage, resolved addresses, target port, TLS-verification setting, WebSocket error, inner exception, authentication result, and RPC method without logging the username, API key, or RPC payload.

For scheduled or manual update-check failures, open **History** and copy the diagnostic value from the failed run. Use that ID to find the matching backend log entries.

### Missing app permissions

Confirm that the service account's group is attached to a custom privilege containing both `APPS_READ` and `APPS_WRITE`. Do not modify TrueNAS built-in privileges.

If app discovery and actions work but a System panel is unavailable, add the role named by that panel: `POOL_READ`, `ALERT_LIST_READ`, `SYSTEM_UPDATE_READ`, or the broader read-only `READONLY_ADMIN`. These permissions do not broaden app write access.

### TrueNAS is unreachable or the connection times out

An unreachable, refused, or timed-out connection means the configured hostname maps to the wrong address, the container is not using the documented host network, or the TrueNAS Web UI is not listening on the configured port.

For an existing installation, edit the custom app, replace its entire YAML with the complete configuration in [Connect the first-launch wizard](#2-connect-the-first-launch-wizard), and save/redeploy. The named `update-manager-data` volume remains attached, so the database and saved settings are preserved. Then open **Settings** and run **Test connection** again.

- `DNS_FAILURE` means the configured hostname is missing from container DNS or `extra_hosts`.
- `NETWORK_UNREACHABLE` means the container has no route to the configured address.
- `CONNECTION_REFUSED` means that address is reachable but no TrueNAS WebSocket service accepted the configured port.

### No apps are discovered

Confirm that the TrueNAS Apps service is running, at least one app is installed, and the service account has `APPS_READ`.

## Security and backup checklist

- Expose the web UI only on a trusted LAN/VPN or behind an authenticated reverse proxy.
- Keep privileged mode disabled. Host networking is the reliable default for TrueNAS loopback access and broadens the container's access to host network services; use the documented bridge alternative only when its connection test succeeds.
- Retain the read-only root filesystem, dropped capabilities, non-root user, and `no-new-privileges` restriction.
- Do not mount `/var/run/docker.sock`.
- Download a password-protected **full recovery JSON** from **Settings → Backup & restore** after configuration changes and store its password separately.
- Persist and back up `/data` only when retaining the complete database and operational history is also required.
- If `APP_ENCRYPTION_KEY` is supplied externally, back it up separately. Losing it makes saved secrets unrecoverable.
- Set `TRUENAS_APP_ID` to the manager's TrueNAS app ID if you want it to block attempts to update itself.

## Additional references

- [TrueNAS API access and user-linked API keys](https://www.truenas.com/docs/scale/api/)
- [TrueNAS custom app installation](https://apps.truenas.com/managing-apps/installing-custom-apps/)
- [TrueNAS role-based access control reference](https://api.truenas.com/v25.10/rbac.html)
