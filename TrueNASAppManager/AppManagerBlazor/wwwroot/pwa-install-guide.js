(() => {
    const Platform = Object.freeze({
        AndroidChrome: "android-chrome",
        AndroidFirefox: "android-firefox",
        AndroidInApp: "android-in-app",
        Ios: "ios",
        SamsungInternet: "samsung-internet",
        Other: "other"
    });

    function classifyPlatform(userAgent) {
        const normalizedUserAgent = (userAgent || "").toLowerCase();
        const isAndroid = normalizedUserAgent.includes("android");

        if (/iphone|ipad|ipod/.test(normalizedUserAgent)) {
            return Platform.Ios;
        }

        if (isAndroid && /(fban|fbav|instagram|; wv\)|\bwv\b|gsa\/)/.test(normalizedUserAgent)) {
            return Platform.AndroidInApp;
        }

        if (isAndroid && normalizedUserAgent.includes("samsungbrowser")) {
            return Platform.SamsungInternet;
        }

        if (isAndroid && /(firefox|fennec)/.test(normalizedUserAgent)) {
            return Platform.AndroidFirefox;
        }

        if (isAndroid && /(chrome|crios|edga|opr)/.test(normalizedUserAgent)) {
            return Platform.AndroidChrome;
        }

        return Platform.Other;
    }

    function getGuidance(platform, isSecureContext) {
        if (!isSecureContext) {
            return {
                platformLabel: "Secure connection required",
                title: "Open the HTTPS App Manager address",
                message: "Android blocks app installation from plain HTTP addresses such as truenas.local and private IPs.",
                steps: [
                    "Open this App Manager through its trusted HTTPS reverse-proxy address in Chrome or Samsung Internet.",
                    "Choose Install app again from the App Manager menu.",
                    "If no HTTPS address exists yet, configure one before trying to install."
                ]
            };
        }

        switch (platform) {
            case Platform.SamsungInternet:
                return {
                    platformLabel: "Samsung Internet",
                    title: "Install on your Galaxy",
                    message: "Samsung Internet can use its own address-bar control instead of opening installation from a page button.",
                    steps: [
                        "Tap the install icon in the address bar. It may appear as a plus or download icon.",
                        "If no icon appears, open the Samsung Internet menu, choose Add page to, then Home screen.",
                        "Confirm Install on Apps screen."
                    ]
                };
            case Platform.AndroidChrome:
                return {
                    platformLabel: "Android browser",
                    title: "Install on Android",
                    message: "Use the browser's installation command when it does not expose a prompt to this page.",
                    steps: [
                        "Open the browser menu in the upper-right corner.",
                        "Choose Install app or Add to Home screen.",
                        "Confirm Install."
                    ]
                };
            case Platform.AndroidFirefox:
                return {
                    platformLabel: "Firefox for Android",
                    title: "Add the App Manager to your phone",
                    message: "Firefox handles web-app installation from its own menu.",
                    steps: [
                        "Open the Firefox menu.",
                        "Choose Install or Add to Home screen.",
                        "Confirm the installation."
                    ]
                };
            case Platform.AndroidInApp:
                return {
                    platformLabel: "In-app browser",
                    title: "Open this page in a full browser",
                    message: "Embedded browsers usually cannot install web apps.",
                    steps: [
                        "Open the browser menu and choose Open in browser.",
                        "Select Samsung Internet or Chrome.",
                        "Return to Install app and confirm the browser prompt."
                    ]
                };
            case Platform.Ios:
                return {
                    platformLabel: "iPhone or iPad",
                    title: "Add the App Manager to your Home Screen",
                    message: "Apple devices install web apps from the browser Share menu.",
                    steps: [
                        "Open the Share menu.",
                        "Choose Add to Home Screen.",
                        "Confirm Add."
                    ]
                };
            default:
                return {
                    platformLabel: "Browser installation",
                    title: "Install TrueNAS App Manager",
                    message: "This browser handles installation from its own menu when a native prompt is unavailable.",
                    steps: [
                        "Open the browser menu.",
                        "Choose Install app or Add to Home Screen.",
                        "Confirm the installation."
                    ]
                };
        }
    }

    const api = Object.freeze({ Platform, classifyPlatform, getGuidance });

    if (typeof window !== "undefined") {
        window.trueNasPwaInstallGuide = api;
    }

    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
})();
