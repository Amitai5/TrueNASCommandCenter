(() => {
    let deferredInstallPrompt = null;
    let isInstalled = isStandalone();

    function isStandalone() {
        return window.matchMedia("(display-mode: standalone)").matches || window.navigator?.standalone === true;
    }

    function isIos() {
        return /iphone|ipad|ipod/i.test(window.navigator?.userAgent ?? "");
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

    async function requestInstall() {
        if (isInstalled) {
            setInstallStatus("TrueNAS App Manager is already installed.");
            return;
        }

        if (deferredInstallPrompt) {
            const prompt = deferredInstallPrompt;
            deferredInstallPrompt = null;
            prompt.prompt();
            await prompt.userChoice;
            updateInstallControls();
            return;
        }

        if (!window.isSecureContext) {
            setInstallStatus("Open this site over HTTPS to install it. HTTP installation is supported only on localhost or 127.0.0.1.");
            return;
        }

        if (isIos()) {
            setInstallStatus("Open the browser Share menu, then choose Add to Home Screen.");
            return;
        }

        setInstallStatus("Use the browser menu and choose Install app or Add to Home Screen.");
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

    window.addEventListener("beforeinstallprompt", (event) => {
        event.preventDefault();
        deferredInstallPrompt = event;
        setInstallStatus("");
        updateInstallControls();
    });

    window.addEventListener("appinstalled", () => {
        deferredInstallPrompt = null;
        isInstalled = true;
        setInstallStatus("TrueNAS App Manager was installed.");
        updateInstallControls();
    });

    document.addEventListener("click", (event) => {
        if (event.target instanceof Element && event.target.closest("[data-pwa-install]")) {
            event.preventDefault();
            void requestInstall();
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

    window.trueNasPwa = Object.freeze({
        install: requestInstall,
        register: registerServiceWorker,
        isInstalled: () => isInstalled
    });

    if (document.readyState === "complete") {
        void registerServiceWorker();
    } else {
        window.addEventListener("load", () => void registerServiceWorker(), { once: true });
    }
})();
