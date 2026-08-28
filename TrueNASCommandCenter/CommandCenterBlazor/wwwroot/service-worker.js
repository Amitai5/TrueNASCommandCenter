const shellCacheName = "truenas-command-center-shell-v3";
const shellCachePrefixes = ["truenas-command-center-shell-", "truenas-app-manager-shell-"];
const offlineUrl = "/offline.html";
const shellAssets = [
    offlineUrl,
    "/app.css",
    "/favicon.svg",
    "/icons/icon-192.png",
    "/icons/icon-512.png",
    "/icons/icon-maskable-512.png"
];

self.addEventListener("install", (event) => {
    event.waitUntil((async () => {
        const cache = await caches.open(shellCacheName);
        await cache.add(offlineUrl);
        await Promise.allSettled(shellAssets
            .filter((asset) => asset !== offlineUrl)
            .map((asset) => cache.add(asset)));
        await self.skipWaiting();
    })());
});

self.addEventListener("activate", (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys
                .filter((key) => shellCachePrefixes.some((prefix) => key.startsWith(prefix)) && key !== shellCacheName)
                .map((key) => caches.delete(key))))
            .then(() => self.clients.claim()));
});

self.addEventListener("fetch", (event) => {
    const request = event.request;
    if (request.method !== "GET") {
        return;
    }

    const url = new URL(request.url);
    if (url.origin !== self.location.origin) {
        return;
    }

    if (request.mode === "navigate") {
        event.respondWith(fetch(request).catch(() => caches.match(offlineUrl)));
        return;
    }

    if (!shellAssets.includes(url.pathname)) {
        return;
    }

    event.respondWith(
        caches.match(request).then((cachedResponse) => {
            const networkResponse = fetch(request).then((response) => {
                if (response.ok) {
                    const responseCopy = response.clone();
                    void caches.open(shellCacheName).then((cache) => cache.put(request, responseCopy));
                }

                return response;
            });

            return cachedResponse || networkResponse;
        }));
});

self.addEventListener("push", (event) => {
    event.waitUntil(self.registration.showNotification("TrueNAS Command Center needs attention", {
        body: "Open the dashboard to review the latest app or system alert.",
        icon: "/icons/icon-192.png",
        badge: "/icons/icon-192.png",
        tag: "truenas-command-center-alert",
        renotify: true,
        data: { url: "/" }
    }));
});

self.addEventListener("notificationclick", (event) => {
    event.notification.close();
    const targetUrl = new URL(event.notification.data?.url || "/", self.location.origin).href;
    event.waitUntil(self.clients.matchAll({ type: "window", includeUncontrolled: true }).then(async (clients) => {
        const existing = clients.find((client) => new URL(client.url).origin === self.location.origin);
        if (existing) {
            await existing.navigate(targetUrl);
            return existing.focus();
        }

        return self.clients.openWindow(targetUrl);
    }));
});
