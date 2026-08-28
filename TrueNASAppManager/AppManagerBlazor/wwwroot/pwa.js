(() => {
    let deferredInstallPrompt = null;
    let isInstalled = isStandalone();
    let pushCallback = null;

    function isStandalone() {
        return window.matchMedia("(display-mode: standalone)").matches || window.navigator?.standalone === true;
    }

    function getInstallPlatform() {
        return window.trueNasPwaInstallGuide?.classifyPlatform(window.navigator?.userAgent ?? "") ?? "other";
    }

    function getInstallGuidance() {
        return window.trueNasPwaInstallGuide?.getGuidance(getInstallPlatform(), window.isSecureContext) ?? {
            platformLabel: "Browser installation",
            title: "Install TrueNAS App Manager",
            message: "Use your browser's installation command to add this app to your device.",
            steps: ["Open the browser menu.", "Choose Install app or Add to Home Screen.", "Confirm the installation."]
        };
    }

    function setInstallStatus(message) {
        document.querySelectorAll("[data-pwa-install-status]").forEach((element) => {
            element.textContent = message;
            element.hidden = !message;
        });
    }

    function updateInstallControls() {
        isInstalled = isInstalled || isStandalone();

        document.querySelectorAll("[data-pwa-install]").forEach((button) => {
            button.hidden = isInstalled;
            button.setAttribute("data-pwa-install-ready", String(Boolean(deferredInstallPrompt)));
            button.setAttribute("title", deferredInstallPrompt ? "Install TrueNAS App Manager" : "Installation options");
        });
    }

    function setReadinessState(selector, isReady, readyText, unavailableText) {
        const element = document.querySelector(selector);
        if (!element) {
            return;
        }

        element.dataset.state = isReady ? "ready" : "blocked";
        const value = element.querySelector("[data-pwa-readiness-value]");
        if (value) {
            value.textContent = isReady ? readyText : unavailableText;
        }
    }

    async function hasValidManifest() {
        const manifestLink = document.querySelector('link[rel~="manifest"]');
        if (!(manifestLink instanceof HTMLLinkElement)) {
            return false;
        }

        try {
            const credentials = manifestLink.crossOrigin === "use-credentials" ? "include" : "omit";
            const response = await window.fetch(manifestLink.href, { cache: "no-store", credentials });
            if (!response.ok) {
                return false;
            }

            const manifest = await response.json();
            const icons = Array.isArray(manifest.icons) ? manifest.icons : [];
            const hasIconAtLeast = (minimumSize) => icons.some((icon) =>
                typeof icon?.sizes === "string" && icon.sizes.split(/\s+/).some((size) => {
                    const match = /^(\d+)x(\d+)$/i.exec(size);
                    return Boolean(match && Number(match[1]) >= minimumSize && Number(match[2]) >= minimumSize);
                }));
            const displayModes = [manifest.display, ...(Array.isArray(manifest.display_override) ? manifest.display_override : [])];

            return Boolean(
                (manifest.name || manifest.short_name) &&
                manifest.start_url &&
                displayModes.some((mode) => mode === "standalone" || mode === "fullscreen" || mode === "minimal-ui") &&
                hasIconAtLeast(192) &&
                hasIconAtLeast(512));
        } catch {
            return false;
        }
    }

    async function getInstallReadiness() {
        const supportsServiceWorker = Boolean(window.navigator && "serviceWorker" in window.navigator);
        const registration = window.isSecureContext && supportsServiceWorker ? await registerServiceWorker() : null;

        return {
            isSecure: window.isSecureContext,
            hasManifest: await hasValidManifest(),
            hasServiceWorker: Boolean(registration?.active || registration?.waiting || registration?.installing),
            hasNativePrompt: Boolean(deferredInstallPrompt)
        };
    }

    function closeInstallOptions() {
        const dialog = document.querySelector("[data-pwa-install-dialog]");
        if (!(dialog instanceof HTMLElement)) {
            return;
        }

        if (typeof dialog.close === "function" && dialog.hasAttribute("open")) {
            dialog.close();
        } else {
            dialog.removeAttribute("open");
        }

        document.body.classList.remove("pwa-install-dialog-open");
    }

    async function showInstallOptions(errorMessage = null) {
        const dialog = document.querySelector("[data-pwa-install-dialog]");
        if (!(dialog instanceof HTMLElement)) {
            setInstallStatus(getInstallGuidance().message);
            return;
        }

        const guidance = getInstallGuidance();
        const readiness = await getInstallReadiness();
        const platformLabel = dialog.querySelector("[data-pwa-install-platform]");
        const title = dialog.querySelector("[data-pwa-install-title]");
        const message = dialog.querySelector("[data-pwa-install-message]");
        const steps = dialog.querySelector("[data-pwa-install-steps]");
        const address = dialog.querySelector("[data-pwa-install-address]");
        const error = dialog.querySelector("[data-pwa-install-error]");
        const retry = dialog.querySelector("[data-pwa-install-retry]");

        if (platformLabel) {
            platformLabel.textContent = guidance.platformLabel;
        }

        if (title) {
            title.textContent = guidance.title;
        }

        if (message) {
            message.textContent = guidance.message;
        }

        if (steps) {
            steps.replaceChildren(...guidance.steps.map((step) => {
                const item = document.createElement("li");
                item.textContent = step;
                return item;
            }));
        }

        if (address) {
            address.textContent = window.location.origin;
        }

        if (error) {
            error.textContent = errorMessage || "";
            error.hidden = !errorMessage;
        }

        if (retry instanceof HTMLButtonElement) {
            retry.hidden = !readiness.hasNativePrompt;
        }

        setReadinessState("[data-pwa-readiness-secure]", readiness.isSecure, "Ready", "HTTPS required");
        setReadinessState("[data-pwa-readiness-manifest]", readiness.hasManifest, "Ready", "Unavailable");
        setReadinessState("[data-pwa-readiness-worker]", readiness.hasServiceWorker, "Ready", "Unavailable");
        setReadinessState("[data-pwa-readiness-method]", true, readiness.hasNativePrompt ? "Install prompt ready" : "Use browser controls", "Use browser controls");

        document.body.classList.add("pwa-install-dialog-open");
        if (!dialog.hasAttribute("open")) {
            if (typeof dialog.showModal === "function") {
                dialog.showModal();
            } else {
                dialog.setAttribute("open", "");
            }
        }

        dialog.querySelector("[data-pwa-install-close]")?.focus();
    }

    async function requestInstall() {
        if (isInstalled) {
            setInstallStatus("TrueNAS App Manager is already installed.");
            return;
        }

        if (deferredInstallPrompt) {
            const prompt = deferredInstallPrompt;
            deferredInstallPrompt = null;
            const userChoice = prompt.userChoice;

            try {
                const promptResult = await prompt.prompt();
                const choice = promptResult ?? (userChoice ? await userChoice : null);
                if (choice?.outcome === "dismissed") {
                    setInstallStatus("Installation was dismissed. Choose Install app again when you are ready.");
                }
            } catch (error) {
                await showInstallOptions(error instanceof Error ? error.message : "The browser could not open its install prompt.");
            } finally {
                updateInstallControls();
            }

            return;
        }

        await showInstallOptions();
    }

    async function registerServiceWorker() {
        if (!window.navigator || !("serviceWorker" in window.navigator) || !window.isSecureContext) {
            return null;
        }

        try {
            return await window.navigator.serviceWorker.register("/service-worker.js", { scope: "/" });
        } catch (error) {
            console.warn("TrueNAS App Manager could not register its offline fallback.", error);
            return null;
        }
    }

    function decodeBase64Url(value) {
        const padding = "=".repeat((4 - (value.length % 4)) % 4);
        const base64 = (value + padding).replace(/-/g, "+").replace(/_/g, "/");
        const raw = window.atob(base64);
        return Uint8Array.from(raw, (character) => character.charCodeAt(0));
    }

    function encodeBase64Url(value) {
        const bytes = new Uint8Array(value);
        let binary = "";
        bytes.forEach((byte) => binary += String.fromCharCode(byte));
        return window.btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    }

    function mapPushSubscription(subscription, deviceName) {
        if (!subscription) {
            return null;
        }

        const p256dh = subscription.getKey("p256dh");
        const auth = subscription.getKey("auth");
        if (!p256dh || !auth) {
            throw new Error("The browser did not return Web Push encryption keys.");
        }

        return {
            endpoint: subscription.endpoint,
            expirationTime: subscription.expirationTime ? new Date(subscription.expirationTime).toISOString() : null,
            p256dh: encodeBase64Url(p256dh),
            auth: encodeBase64Url(auth),
            deviceName: deviceName || null,
            userAgent: window.navigator.userAgent || null
        };
    }

    function supportsPush() {
        return Boolean(window.isSecureContext && "serviceWorker" in window.navigator && "PushManager" in window && "Notification" in window);
    }

    async function getPushState() {
        if (!supportsPush()) {
            return {
                supported: false,
                secureContext: window.isSecureContext,
                permission: "unsupported",
                subscription: null,
                error: window.isSecureContext ? "This browser does not support Web Push." : "Open the App Manager over HTTPS to enable push notifications."
            };
        }

        const registration = await registerServiceWorker();
        if (!registration) {
            return { supported: false, secureContext: true, permission: Notification.permission, subscription: null, error: "The service worker could not be registered." };
        }

        const subscription = await registration.pushManager.getSubscription();
        return {
            supported: true,
            secureContext: true,
            permission: Notification.permission,
            subscription: mapPushSubscription(subscription, null),
            error: null
        };
    }

    async function subscribePush(publicKey, deviceName) {
        if (!supportsPush()) {
            return await getPushState();
        }

        let permission = Notification.permission;
        if (permission === "default") {
            permission = await Notification.requestPermission();
        }

        if (permission !== "granted") {
            return { supported: true, secureContext: true, permission, subscription: null, error: "Notification permission was not granted." };
        }

        const registration = await registerServiceWorker();
        if (!registration) {
            return { supported: false, secureContext: true, permission, subscription: null, error: "The service worker could not be registered." };
        }

        const applicationServerKey = decodeBase64Url(publicKey);
        let subscription = await registration.pushManager.getSubscription();
        const existingKey = subscription?.options?.applicationServerKey;
        if (subscription && existingKey) {
            const savedKey = new Uint8Array(existingKey);
            const matches = savedKey.length === applicationServerKey.length && savedKey.every((value, index) => value === applicationServerKey[index]);
            if (!matches) {
                await subscription.unsubscribe();
                subscription = null;
            }
        }

        subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey });
        return {
            supported: true,
            secureContext: true,
            permission,
            subscription: mapPushSubscription(subscription, deviceName),
            error: null
        };
    }

    async function unsubscribePush() {
        if (!("serviceWorker" in window.navigator)) {
            return { endpoint: null, unsubscribed: false };
        }

        const registration = await window.navigator.serviceWorker.ready;
        const subscription = await registration.pushManager.getSubscription();
        if (!subscription) {
            return { endpoint: null, unsubscribed: false };
        }

        const endpoint = subscription.endpoint;
        const unsubscribed = await subscription.unsubscribe();
        return { endpoint, unsubscribed };
    }

    function setPushCallback(callback) {
        pushCallback = callback;
    }

    function clearPushCallback() {
        pushCallback = null;
    }

    async function reportPushAction(action, state, endpoint) {
        if (pushCallback) {
            await pushCallback.invokeMethodAsync("OnPushBrowserActionAsync", action, state, endpoint || null);
        }
    }

    window.addEventListener("beforeinstallprompt", (event) => {
        event.preventDefault();
        deferredInstallPrompt = event;
        setInstallStatus("");
        updateInstallControls();

        const retry = document.querySelector("[data-pwa-install-retry]");
        if (retry instanceof HTMLButtonElement) {
            retry.hidden = false;
        }

        setReadinessState("[data-pwa-readiness-method]", true, "Install prompt ready", "Use browser controls");
    });

    window.addEventListener("appinstalled", () => {
        deferredInstallPrompt = null;
        isInstalled = true;
        setInstallStatus("TrueNAS App Manager was installed.");
        closeInstallOptions();
        updateInstallControls();
    });

    document.addEventListener("click", (event) => {
        if (!(event.target instanceof Element)) {
            return;
        }

        const installButton = event.target.closest("[data-pwa-install]");
        if (installButton instanceof HTMLButtonElement) {
            event.preventDefault();
            installButton.disabled = true;
            void requestInstall().finally(() => installButton.disabled = false);
            return;
        }

        if (event.target.closest("[data-pwa-install-close]")) {
            event.preventDefault();
            closeInstallOptions();
            return;
        }

        const installRetry = event.target.closest("[data-pwa-install-retry]");
        if (installRetry instanceof HTMLButtonElement) {
            event.preventDefault();
            installRetry.disabled = true;
            closeInstallOptions();
            void requestInstall().finally(() => installRetry.disabled = false);
            return;
        }

        if (event.target.matches("[data-pwa-install-dialog]")) {
            closeInstallOptions();
            return;
        }

        const enableButton = event.target.closest("[data-push-enable]");
        if (enableButton instanceof HTMLButtonElement) {
            event.preventDefault();
            enableButton.disabled = true;
            const deviceName = document.querySelector("#push-device-name")?.value || null;
            void subscribePush(enableButton.dataset.pushPublicKey || "", deviceName)
                .then((state) => reportPushAction("enable", state, null))
                .catch((error) => reportPushAction("enable", {
                    supported: supportsPush(),
                    secureContext: window.isSecureContext,
                    permission: "Notification" in window ? Notification.permission : "unsupported",
                    subscription: null,
                    error: error instanceof Error ? error.message : "Push subscription failed."
                }, null))
                .finally(() => enableButton.disabled = false);
            return;
        }

        const disableButton = event.target.closest("[data-push-disable]");
        if (disableButton instanceof HTMLButtonElement) {
            event.preventDefault();
            disableButton.disabled = true;
            void unsubscribePush()
                .then(async (result) => {
                    const state = await getPushState();
                    if (result.endpoint && !result.unsubscribed) {
                        await reportPushAction("error", { ...state, error: "The browser could not remove its push subscription." }, null);
                        return;
                    }

                    await reportPushAction("disable", state, result.endpoint);
                })
                .catch((error) => reportPushAction("disable", {
                    supported: supportsPush(),
                    secureContext: window.isSecureContext,
                    permission: "Notification" in window ? Notification.permission : "unsupported",
                    subscription: null,
                    error: error instanceof Error ? error.message : "Push unsubscribe failed."
                }, null))
                .finally(() => disableButton.disabled = false);
        }
    });

    if (window.MutationObserver) {
        const installControlObserver = new window.MutationObserver((records) => {
            const hasNewInstallControl = records.some((record) =>
                Array.from(record.addedNodes).some((node) =>
                    node instanceof Element &&
                    (node.matches("[data-pwa-install]") || node.querySelector("[data-pwa-install]"))));

            if (hasNewInstallControl) {
                updateInstallControls();
            }
        });

        installControlObserver.observe(document.body, { childList: true, subtree: true });
    }

    updateInstallControls();

    const installDialog = document.querySelector("[data-pwa-install-dialog]");
    installDialog?.addEventListener("close", () => document.body.classList.remove("pwa-install-dialog-open"));

    window.trueNasPwa = Object.freeze({
        install: requestInstall,
        register: registerServiceWorker,
        isInstalled: () => isInstalled,
        getPushState,
        subscribePush,
        unsubscribePush,
        setPushCallback,
        clearPushCallback
    });

    if (document.readyState === "complete") {
        void registerServiceWorker();
    } else {
        window.addEventListener("load", () => void registerServiceWorker(), { once: true });
    }
})();
