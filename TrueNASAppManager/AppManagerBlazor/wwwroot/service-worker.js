const shellCacheName = "truenas-app-manager-shell-v1";
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
    event.waitUntil(caches.open(shellCacheName).then((cache) => cache.addAll(shellAssets)));
    self.skipWaiting();
});

self.addEventListener("activate", (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys
                .filter((key) => key.startsWith("truenas-app-manager-shell-") && key !== shellCacheName)
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
