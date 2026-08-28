(() => {
    const storageKey = "truenas-command-center-theme";
    const legacyStorageKey = "truenas-app-manager-theme";
    const darkThemeQuery = window.matchMedia("(prefers-color-scheme: dark)");
    let preference = readPreference();

    function readPreference() {
        try {
            const currentPreference = localStorage.getItem(storageKey);
            const legacyPreference = localStorage.getItem(legacyStorageKey);
            const storedPreference = currentPreference ?? legacyPreference;
            if (currentPreference === null && (legacyPreference === "light" || legacyPreference === "dark")) {
                localStorage.setItem(storageKey, legacyPreference);
            }

            return storedPreference === "light" || storedPreference === "dark" ? storedPreference : null;
        } catch {
            return null;
        }
    }

    function resolveTheme() {
        return preference ?? (darkThemeQuery.matches ? "dark" : "light");
    }

    function applyTheme() {
        const theme = resolveTheme();
        const root = document.documentElement;

        root.dataset.theme = theme;
        root.style.colorScheme = theme;
        document.getElementById("theme-color")?.setAttribute("content", theme === "dark" ? "#0d1118" : "#f4f6fa");
        updateThemeButtons(theme);
    }

    function updateThemeButtons(theme) {
        const nextTheme = theme === "dark" ? "light" : "dark";

        document.querySelectorAll("[data-theme-toggle]").forEach((button) => {
            button.setAttribute("aria-pressed", String(theme === "dark"));
            button.setAttribute("aria-label", `Switch to ${nextTheme} theme`);
            button.setAttribute("title", `Switch to ${nextTheme} theme`);

            const icon = button.querySelector("[data-theme-icon]");
            const label = button.querySelector("[data-theme-label]");

            if (icon) {
                icon.textContent = theme === "dark" ? "☀" : "☾";
            }

            if (label) {
                label.textContent = "Theme";
            }
        });
    }

    function savePreference(theme) {
        preference = theme;

        try {
            localStorage.setItem(storageKey, theme);
        } catch {
            // The in-memory preference still keeps the toggle functional for this page.
        }
    }

    function toggleTheme() {
        savePreference(resolveTheme() === "dark" ? "light" : "dark");
        applyTheme();
    }

    document.addEventListener("click", (event) => {
        if (event.target instanceof Element && event.target.closest("[data-theme-toggle]")) {
            toggleTheme();
        }
    });

    darkThemeQuery.addEventListener("change", () => {
        if (preference === null) {
            applyTheme();
        }
    });

    window.addEventListener("storage", (event) => {
        if (event.key === storageKey) {
            preference = readPreference();
            applyTheme();
        }
    });

    const themeControlObserver = new MutationObserver((records) => {
        const hasNewThemeControl = records.some((record) =>
            Array.from(record.addedNodes).some((node) =>
                node instanceof Element &&
                (node.matches("[data-theme-toggle]") || node.querySelector("[data-theme-toggle]"))));

        if (hasNewThemeControl) {
            updateThemeButtons(resolveTheme());
        }
    });

    themeControlObserver.observe(document.body, { childList: true, subtree: true });

    applyTheme();
})();
