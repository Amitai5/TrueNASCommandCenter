# TrueNAS API Permission Guide

[Back to the main README](../../README.md) · [First-time setup guide](SETUP.md) · [Developer guide](DEVELOPMENT.md)

TrueNAS Command Center uses a user-linked API key whose owner belongs to a local group with a custom privilege. Keep the privilege limited to the features you intend to use.

## Quick answer

For app management and the Discover gallery, grant:

- `APPS_READ`
- `APPS_WRITE`
- `CATALOG_READ`

`CATALOG_READ` is separate from `APPS_READ`. Adding the role does not update an already-authenticated WebSocket session. After changing a privilege, run **Settings → Connection → Test connection** or select **Discover → Reconnect & retry**.

## Role matrix

| Role | Classification | Enables | Behavior when omitted |
| --- | --- | --- | --- |
| `APPS_READ` | Required | Installed-app inventory, state, workloads, ports, portals, logs, live statistics, update summaries, and rollback versions | Core inventory and management views cannot load |
| `APPS_WRITE` | Required for management | Start, stop, restart, app upgrade, image refresh, and rollback | The app can be read-only, but lifecycle and update actions fail or remain unavailable |
| `CATALOG_READ` | Required for Discover | Catalog gallery, catalog details, and similar-app suggestions | Discover reports a permission error; installed-app inventory still works |
| `POOL_READ` | Optional | Pool health, capacity, and fragmentation | Pool cards show an unavailable state |
| `ALERT_LIST_READ` | Optional | Native TrueNAS alert list | The alerts panel shows an unavailable state |
| `SYSTEM_UPDATE_READ` | Optional | TrueNAS operating-system update status | The OS update panel shows an unavailable state |
| `DATASET_READ` | Optional | Data Protection dataset tree and coverage base | Dataset coverage is unavailable; protection tasks can still load |
| `SNAPSHOT_READ` | Optional | Snapshot counts and newest snapshot age | Snapshot age and counts are unavailable |
| `SNAPSHOT_TASK_READ` | Optional | Periodic snapshot coverage, state, and schedule | Snapshot-task cards and coverage are unavailable |
| `REPLICATION_TASK_READ` | Optional | Replication coverage, state, source/target, and schedule | Replication cards and coverage are unavailable |
| `CLOUD_SYNC_READ` | Optional | Cloud-sync coverage, state, local path, and schedule | Cloud-sync cards and coverage are unavailable |
| `DISK_READ` | Optional | Disk model, serial, capacity, bus, type, rotation, and SMART configuration | Drive inventory is unavailable |
| `REPORTING_READ` | Optional | Cached disk temperature and device-reported critical thresholds | Drive inventory remains visible without temperatures |
| `READONLY_ADMIN` | Optional and broad | Host identity, TrueNAS version, CPU, memory, load, boot time, and uptime; TrueNAS expands it to the available read-only roles | Host details remain limited; use the focused roles above when broad read access is unnecessary |

The Operations Inbox reuses these permissions: `ALERT_LIST_READ` supplies native alerts and `POOL_READ` supplies pool scrub/resilver activity. Local update failures, notification failures, and Uptime Kuma outages need no additional TrueNAS role. The authenticated `core.get_jobs` call returns jobs owned by the current API session for a scoped account. Seeing jobs owned by every TrueNAS session requires a **Full Admin account**, not an additional focused role. Full Admin is optional and substantially broader than the profiles below.

TrueNAS systems enforcing a STIG profile do not permit write roles. Such a connection can provide read-only visibility but cannot perform lifecycle or update actions.

## Recommended privilege profiles

### Core app management

Use when Discover and optional host panels are not needed:

```text
APPS_READ
APPS_WRITE
```

### Core management plus Discover

Recommended for the normal Command Center experience:

```text
APPS_READ
APPS_WRITE
CATALOG_READ
```

### Focused system visibility

Adds each read-only panel without granting broad administrator visibility:

```text
APPS_READ
APPS_WRITE
CATALOG_READ
POOL_READ
ALERT_LIST_READ
SYSTEM_UPDATE_READ
```

### Complete focused read-only centers

Adds the Data Protection and Drive & Pool Health sources without granting broad administrator visibility:

```text
APPS_READ
APPS_WRITE
CATALOG_READ
POOL_READ
ALERT_LIST_READ
SYSTEM_UPDATE_READ
DATASET_READ
SNAPSHOT_READ
SNAPSHOT_TASK_READ
REPLICATION_TASK_READ
CLOUD_SYNC_READ
DISK_READ
REPORTING_READ
```

### Full read visibility

Use only when the broader read surface is acceptable:

```text
READONLY_ADMIN
APPS_WRITE
```

On current TrueNAS role definitions, `READONLY_ADMIN` includes the read roles needed by Apps, Discover, pools, alerts, and system update status. `APPS_WRITE` remains separate.

## Features that do not need another TrueNAS role

- Uptime Kuma import uses the Uptime Kuma Prometheus API key.
- GitHub enrichment reads public GitHub metadata.
- Generic webhooks and browser push contact their configured external services.
- Favorites, groups, policies, schedules, history, and recovery backups are local Command Center data.
- TrueNAS-native email uses the authenticated middleware connection and the mail service already configured in TrueNAS; the supported deployment does not require adding another role to this privilege.
- Operations Inbox acknowledgement, resolution state, local app-update failures, Uptime Kuma outages, and notification failures are local Command Center data.

## Apply a role change

1. In TrueNAS, open **Credentials → Groups → Privileges**.
2. Edit the custom privilege assigned to the API key owner's local group.
3. Add or remove roles and save.
4. In Command Center, open **Settings → Connection** and select **Test connection**. For a Discover-only failure, **Reconnect & retry** performs the same session reset before loading the catalog.

The reconnect step matters because TrueNAS authorizes the WebSocket when the session is created. Repeating a catalog request on the old session can return the same denial even after the privilege was edited.

## Troubleshooting Discover

If installed apps load but Discover reports **Catalog unavailable**:

1. Confirm the privilege contains `CATALOG_READ`, not only `APPS_READ`.
2. Select **Reconnect & retry** to create a new authenticated session.
3. If it still fails, copy the diagnostic ID shown on the page and search the Command Center container logs for it.
4. Verify TrueNAS itself has synchronized its Apps catalog.

The browser console only confirms that the Blazor user interface is connected to Command Center. It does not prove that the server-side Command Center process is authorized to call the TrueNAS catalog API.

## TrueNAS API references

- [`catalog.apps`](https://api.truenas.com/v25.04.2/api_methods_catalog.apps.html), [`catalog.get_app_details`](https://api.truenas.com/v25.04.2/api_methods_catalog.get_app_details.html), and [`app.similar`](https://api.truenas.com/v25.04.2/api_methods_app.similar.html) require `CATALOG_READ`.
- [`alert.list`](https://api.truenas.com/v25.04.2/api_methods_alert.list.html) requires `ALERT_LIST_READ`.
- [`update.status`](https://api.truenas.com/v25.04.2/api_events_update.status.html) requires `SYSTEM_UPDATE_READ`.
- [`pool.query`](https://api.truenas.com/v25.10/api_methods_pool.query.html) requires `POOL_READ`.
- [`pool.dataset.query`](https://api.truenas.com/v25.10.0/api_methods_pool.dataset.query.html) requires `DATASET_READ`, and [`pool.snapshot.query`](https://api.truenas.com/v25.10.0/api_methods_pool.snapshot.query.html) requires `SNAPSHOT_READ`.
- [`pool.snapshottask.query`](https://api.truenas.com/v25.10.0/api_methods_pool.snapshottask.query.html), [`replication.query`](https://api.truenas.com/v25.10.0/api_methods_replication.query.html), and [`cloudsync.query`](https://api.truenas.com/v25.10.0/api_methods_cloudsync.query.html) require `SNAPSHOT_TASK_READ`, `REPLICATION_TASK_READ`, and `CLOUD_SYNC_READ` respectively.
- [`disk.query`](https://api.truenas.com/v25.10.0/api_methods_disk.query.html) requires `DISK_READ`; [`disk.temperatures`](https://api.truenas.com/v25.10/api_methods_disk.temperatures.html) requires `REPORTING_READ` and may return cached values.
- [`system.info`](https://api.truenas.com/v25.10.0/api_methods_system.info.html) requires `READONLY_ADMIN`.
- [`core.get_jobs`](https://api.truenas.com/v25.10.0/api_methods_core.get_jobs.html) returns only jobs owned by the authenticated session unless that account is Full Admin.
- The [TrueNAS role reference](https://api.truenas.com/v25.10/rbac.html) documents role composition and the breadth of `READONLY_ADMIN`.
