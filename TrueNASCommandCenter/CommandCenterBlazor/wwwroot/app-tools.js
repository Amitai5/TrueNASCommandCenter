(() => {
    const fullscreenObservers = new Map();

    async function copyText(value) {
        try {
            if (navigator.clipboard?.writeText) {
                await navigator.clipboard.writeText(value);
                return true;
            }

            const textarea = document.createElement("textarea");
            textarea.value = value;
            textarea.setAttribute("readonly", "");
            textarea.style.position = "fixed";
            textarea.style.opacity = "0";
            document.body.appendChild(textarea);
            textarea.select();
            const copied = document.execCommand("copy");
            textarea.remove();
            return copied;
        } catch {
            return false;
        }
    }

    async function toggleFullscreen(elementId) {
        const element = document.getElementById(elementId);
        if (!element || !document.fullscreenEnabled || typeof element.requestFullscreen !== "function") {
            return false;
        }

        try {
            if (document.fullscreenElement === element) {
                await document.exitFullscreen();
            } else {
                if (document.fullscreenElement) {
                    await document.exitFullscreen();
                }

                await element.requestFullscreen();
            }

            return true;
        } catch {
            return false;
        }
    }

    function registerLogFullscreen(elementId, dotNetReference) {
        unregisterLogFullscreen(elementId);
        const fullscreenHandler = () => {
            dotNetReference.invokeMethodAsync("OnLogFullscreenChanged", document.fullscreenElement?.id === elementId).catch(() => {});
        };
        const keyHandler = (event) => {
            if (event.key === "Escape") {
                dotNetReference.invokeMethodAsync("OnLogFullscreenEscape").catch(() => {});
            }
        };

        document.addEventListener("fullscreenchange", fullscreenHandler);
        document.addEventListener("keydown", keyHandler);
        fullscreenObservers.set(elementId, { fullscreenHandler, keyHandler });
    }

    function unregisterLogFullscreen(elementId) {
        const handlers = fullscreenObservers.get(elementId);
        if (!handlers) {
            return;
        }

        document.removeEventListener("fullscreenchange", handlers.fullscreenHandler);
        document.removeEventListener("keydown", handlers.keyHandler);
        fullscreenObservers.delete(elementId);
    }

    function followLogTail(elementId) {
        const element = document.getElementById(elementId);
        if (!element) {
            return;
        }

        const distanceFromBottom = element.scrollHeight - element.clientHeight - element.scrollTop;
        if (distanceFromBottom < 80) {
            element.scrollTop = element.scrollHeight;
        }
    }

    function downloadText(fileName, content, contentType) {
        const blob = new Blob([content], { type: contentType ?? "application/json" });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement("a");
        anchor.href = url;
        anchor.download = fileName;
        anchor.style.display = "none";
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        setTimeout(() => URL.revokeObjectURL(url), 0);
    }

    window.trueNasCommandCenter = {
        copyText,
        downloadText,
        followLogTail,
        registerLogFullscreen,
        toggleFullscreen,
        unregisterLogFullscreen
    };
})();
