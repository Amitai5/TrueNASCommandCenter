# First-Time Setup Guide

[Back to the main README](../README.md) · [Developer guide](DEVELOPMENT.md)

This guide walks through installing TrueNAS App Update Manager, creating a least-privilege TrueNAS service account, connecting the first-launch wizard, and resolving the most common setup problems. The TrueNAS navigation names below follow Community Edition / SCALE 25.10 and later.

## Before you begin

You need:

- TrueNAS Community Edition / SCALE 25.10 or later with the Apps service running.
- An administrator account that can create users, groups, privileges, and API keys.
- Network access from your browser to port `2600` on the TrueNAS system, or another port configured through `x-app-port` in the YAML.
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

The username is case-sensitive. You will enter this exact username in the update manager.

### Grant only the app roles

1. Open **Credentials → Groups**.
2. Click **Privileges**, then **Add**.
3. Name the privilege something descriptive, such as `TrueNAS App Update Manager`.
4. Under **Local Groups**, select the service account's primary group.
5. Under **Roles**, select:
   - `APPS_READ`
   - `APPS_WRITE`
6. Leave **Web Shell Access** disabled and save the privilege.

`APPS_READ` allows discovery and status checks. `APPS_WRITE` allows upgrades, image refreshes, and rollbacks. TrueNAS systems with a STIG security profile do not permit write roles; those systems cannot perform automatic app updates through this account.

### Create the API key

1. Return to **Credentials → Users**.
2. Select the service account.
3. Click **Add API Key**, or open **View API Keys** and then click **Add API Key**.
4. Enter a descriptive key name and choose an expiration policy.
5. Save the key and copy it immediately.

TrueNAS displays the complete key only when it is created or reset. If it is lost, reset the key and update the saved key in the manager under **Settings → TrueNAS connection**.

## 2. Connect the first-launch wizard

Open the update manager and complete **Step 1: TrueNAS connection**.

| Field | Recommended value |
| --- | --- |
| WebSocket URL | `wss://<truenas-hostname>/api/current` |
| Username | The API key owner's exact username, such as `autoupdate` |
| API key | The key copied from TrueNAS |
| Verify TLS certificate | Enabled when the hostname and certificate are valid |
| Allow insecure `ws://` | Disabled |

Use a hostname that resolves from inside the app container. A fixed IP address can work, but TLS verification also requires the certificate to match that IP address.

TrueNAS requires secure transport for user-linked API keys. Do not use `ws://` on a current TrueNAS system: an authentication attempt over insecure transport can revoke the key. The insecure option exists only for legacy or isolated test environments.

Click **Test connection**. A successful test verifies authentication, pings middleware, checks app access, and enables **Continue**. The Continue button intentionally remains disabled until the connection test succeeds.

If TrueNAS uses an untrusted or self-signed certificate, the preferred fix is to configure a trusted certificate and use its matching hostname. On a trusted LAN, disabling certificate verification can be used as a temporary workaround while keeping `wss://` encryption enabled.

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

- Email requires an SMTP host, port, security mode, sender, and recipients.
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

## Troubleshooting

### Test connection or Continue does nothing

1. Hard-refresh the browser with `Ctrl+Shift+R` or clear the site cache.
2. Confirm the running container uses the newest image, then redeploy it if necessary.
3. In browser developer tools, verify `/_framework/blazor.web.js` returns HTTP `200` as JavaScript.

The page can render as static HTML even when the Blazor bootstrap script fails. In that state, form fields appear normally but button handlers are not connected.

### TrueNAS keeps using an older image

1. Edit the custom app YAML and add `pull_policy: always` directly below the `image:` line.
2. Open **Apps → Configuration → Settings** and enable **Check for docker image updates**.
3. Save/redeploy the custom app while preserving the existing `/data` volume.

`pull_policy: always` tells Compose to check GHCR whenever TrueNAS applies or recreates the app. It does not periodically restart a running container. If TrueNAS still has not detected the new digest, use **Apps → Configuration → Manage Container Images → Pull Image**, pull `ghcr.io/amitai5/truenasautoupdater:latest`, and then save/redeploy the custom app.

### Continue is disabled

This is expected until **Test connection** completes successfully. Read the status message above the form and correct the reported connection, authentication, certificate, or role error.

### Certificate validation failed

- Verify that the WebSocket hostname matches the certificate.
- Configure a trusted TrueNAS certificate when possible.
- For a temporary trusted-LAN workaround, keep `wss://` and disable only certificate verification.

### Authentication failed

- Confirm the username is the account that owns the API key.
- API keys are shown only once; reset the key if the saved value is uncertain.
- If the key was used with `ws://`, check whether TrueNAS revoked it and create or reset it.

### Collect connection diagnostics

Every failed **Test connection** now displays an error code and diagnostic ID. Open the TrueNAS app's container logs and search for that diagnostic ID. The matching entries show the connection stage, resolved addresses, target port, TLS-verification setting, WebSocket error, inner exception, authentication result, and RPC method without logging the username, API key, or RPC payload.

For scheduled or manual update-check failures, open **History** and copy the diagnostic value from the failed run. Use that ID to find the matching backend log entries.

### Missing app permissions

Confirm that the service account's group is attached to a custom privilege containing both `APPS_READ` and `APPS_WRITE`. Do not modify TrueNAS built-in privileges.

### Hostname cannot be resolved or connection times out

The hostname must resolve from the container, not only from your desktop computer. Try a resolvable FQDN or the TrueNAS IP address, confirm port `443` is reachable, and verify that app networking does not overlap your LAN subnet.

### No apps are discovered

Confirm that the TrueNAS Apps service is running, at least one app is installed, and the service account has `APPS_READ`.

## Security and backup checklist

- Expose the web UI only on a trusted LAN/VPN or behind an authenticated reverse proxy.
- Keep privileged mode and host networking disabled.
- Do not mount `/var/run/docker.sock`.
- Persist and back up `/data`, which contains the database and generated encryption key.
- If `APP_ENCRYPTION_KEY` is supplied externally, back it up separately. Losing it makes saved secrets unrecoverable.
- Set `TRUENAS_APP_ID` to the manager's TrueNAS app ID if you want it to block attempts to update itself.

## Additional references

- [TrueNAS API access and user-linked API keys](https://www.truenas.com/docs/scale/api/)
- [TrueNAS custom app installation](https://apps.truenas.com/managing-apps/installing-custom-apps/)
- [TrueNAS role-based access control reference](https://api.truenas.com/v25.10/rbac.html)
