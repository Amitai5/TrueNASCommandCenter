# First-Time Setup Guide

[Back to the main README](../README.md) · [Developer guide](DEVELOPMENT.md)

This guide walks through installing TrueNAS App Manager, creating a least-privilege TrueNAS service account, connecting the first-launch wizard, and resolving the most common setup problems. The TrueNAS navigation names below follow Community Edition / SCALE 25.10 and later.

## Before you begin

You need:

- TrueNAS Community Edition / SCALE 25.10 or later with the Apps service running.
- An administrator account that can create users, groups, privileges, and API keys.
- Network access from your browser to port `2600` on the TrueNAS system, or another port configured through `x-app-port` in the YAML.
- The current TrueNAS Web UI IPv4 address and the hostname covered by its TLS certificate.
- A trusted LAN or VPN. The manager does not include its own user login or RBAC.

Install the container first using the [TrueNAS Custom App or Docker instructions](../README.md#installation). After installation, open:

```text
http://<truenas-address>:2600
```

The liveness endpoint should return a successful response at `http://<truenas-address>:2600/health/live`.

## 1. Create a service account and API key

Use a dedicated account instead of an administrator's personal API key. API keys provide password-equivalent middleware access and should be stored securely.

### Create the account

1. In TrueNAS, open **Credentials → Users**.
2. Click **Add**.
3. Enter a descriptive full name and a username such as `autoupdate`.
4. Use a strong random password and keep **Shell Access** and **SSH Access** disabled.
5. Create or select a dedicated primary group for the account, then save it.

The username is case-sensitive. You will enter this exact username in TrueNAS App Manager.

### Grant only the app roles

1. Open **Credentials → Groups**.
2. Click **Privileges**, then **Add**.
3. Name the privilege something descriptive, such as `TrueNAS App Manager`.
4. Under **Local Groups**, select the service account's primary group.
5. Under **Roles**, select:
   - `APPS_READ`
   - `APPS_WRITE`
6. Leave **Web Shell Access** disabled and save the privilege.

`APPS_READ` allows discovery, health, ports, portals, containers, and logs. `APPS_WRITE` allows starts, stops, restarts, upgrades, image refreshes, and rollbacks. TrueNAS systems with a STIG security profile do not permit write roles; those systems cannot perform lifecycle or update actions through this account.

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

Email and generic webhook notifications are optional. Enable only the providers and event types you want.

- Email uses the existing TrueNAS mail configuration through the authenticated `mail.send` method. Leave recipients blank to use TrueNAS administrator addresses, or enter explicit recipients.
- Webhooks require an HTTPS endpoint and can include an Authorization value or secret headers.
- Use the test button for each configured provider before continuing.

Secrets are encrypted before being stored. Leaving a saved secret field blank preserves the existing value.

## 5. Discover apps and assign policies

The final wizard step performs read-only discovery. It does not update applications.

Every discovered app starts with an **Unconfigured** policy. Review each app and choose one of:

- **Auto Update** — apply updates automatically within the selected version scope.
- **Notify Only** — report an available update without installing it.
- **Ignore** — keep the app visible but take no update action.

This fail-closed default prevents newly discovered applications from updating without an explicit policy.

## 6. Manage, monitor, and inspect apps

Open an app from the **Apps** page to start, stop, or restart it through TrueNAS. The app list also provides a quick **Start** action for stopped or crashed apps and a **Restart** action for running apps. Lifecycle actions wait for the TrueNAS job to finish before refreshing the displayed state.

To monitor an app, open **Edit policy** and choose **Notify only** or **Restart once and notify** under **When this app is down**. Health checks include the top-level app state and reported containers. Each incident sends one downtime event; recovery sends a separate event. Automatic recovery is attempted at most once per incident. Stops initiated from this manager enter maintenance mode and do not alert. A completed `permissions` initialization workload is neutral and appears as **Exited normally** rather than degrading a running app.

The app-details page shows published ports, safe Web UI and source links, versions, train, containers, images, networks, volumes, recent lifecycle/update history, and on-demand live logs. Logs are bounded to 500 lines in browser memory and are never persisted. Use **Copy all** for ISO-8601 plain text or **Fullscreen** for a focused console. Optional GitHub enrichment is disabled by default and only queries canonical public `github.com` sources.

Configure separate **Local Web UI URL** and **Remote Web UI URL** values under **Edit policy** when an app is available through different addresses. Local manager hosts such as `truenas.local`, localhost, and private IP addresses use the local route. Public manager domains use only the explicitly configured remote route; the manager does not guess subdomains. The global **Local TrueNAS Web UI host** setting can rewrite TrueNAS portals and supply the hostname for published local ports.

## 7. Back up and restore configuration

Open **Settings → Backup & restore** and choose the appropriate portable export:

- **Download safe JSON** backs up global settings and per-app policies, downtime behavior, maintenance settings, notification overrides, and local/remote Web UI URLs without secrets.
- **Download encrypted JSON** includes the saved TrueNAS API key and webhook secrets. Enter and confirm a password; the file is authenticated and encrypted with AES-256-GCM using a PBKDF2-SHA256-derived key.

To restore, select a JSON file up to 2 MB, enter its password if encrypted, select **Validate & preview**, review the number of app configurations, and confirm the import. Restore is a transactional merge by app ID: unlisted apps and existing history remain unchanged. Settings for apps not yet discovered are retained and applied when the next inventory refresh finds them. A safe restore preserves secrets already stored on the destination; a full encrypted restore replaces the secrets contained in the backup.

Portable configuration JSON does not contain inventory, logs, health incidents, GitHub cache, notification deliveries, or update history. Back up the persistent `/data` volume for a complete database/history recovery, and separately protect `APP_ENCRYPTION_KEY` when you provide it externally.

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
- Download portable configuration JSON from **Settings → Backup & restore** for policy and settings recovery.
- Persist and back up `/data`, which contains the complete database, history, and generated encryption key.
- If `APP_ENCRYPTION_KEY` is supplied externally, back it up separately. Losing it makes saved secrets unrecoverable.
- Set `TRUENAS_APP_ID` to the manager's TrueNAS app ID if you want it to block attempts to update itself.

## Additional references

- [TrueNAS API access and user-linked API keys](https://www.truenas.com/docs/scale/api/)
- [TrueNAS custom app installation](https://apps.truenas.com/managing-apps/installing-custom-apps/)
- [TrueNAS role-based access control reference](https://api.truenas.com/v25.10/rbac.html)
