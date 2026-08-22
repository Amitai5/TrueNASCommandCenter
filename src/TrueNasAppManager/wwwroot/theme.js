(() => {
    const storageKey = "truenas-app-manager-theme";
    const darkThemeQuery = window.matchMedia("(prefers-color-scheme: dark)");
    let preference = readPreference();

    function readPreference() {
        try {
            const storedPreference = localStorage.getItem(storageKey);
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
        const nextTheme = theme === "dark" ? "light" : "dark";
        const root = document.documentElement;

        root.dataset.theme = theme;
        root.style.colorScheme = theme;
        document.getElementById("theme-color")?.setAttribute("content", theme === "dark" ? "#0f1117" : "#fafafc");

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

    applyTheme();
})();
