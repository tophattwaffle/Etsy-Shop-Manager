window.esm = {
    // Clipboard API requires a secure context (HTTPS via HAProxy, or localhost).
    copy: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    }
};
