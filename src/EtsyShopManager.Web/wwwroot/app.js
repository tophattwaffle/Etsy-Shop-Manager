window.esm = {
    // How long a reconnect may take before the reconnect dialog opens.
    reconnectGraceMs: 1500,

    // Gaps between reconnect attempts. Blazor's default is ten instant attempts, then 5 s apart; a phone coming back
    // to the tab often fails the instant ones (its network is still waking) and then sat out the full 5 s. These start
    // fast and back off, then settle at 5 s for a server restarting after a deploy (about 2.5 minutes in all).
    reconnectDelayMs: function (previousAttempts, maxRetries) {
        if (maxRetries && previousAttempts >= maxRetries) return null;
        return [0, 250, 500, 750, 1000, 1000, 2000, 2000, 3000][previousAttempts] ?? 5000;
    },

    startBlazor: function () {
        Blazor.start({
            circuit: {
                reconnectionOptions: {
                    maxRetries: 36,
                    retryIntervalMilliseconds: esm.reconnectDelayMs,
                    dialogId: "components-reconnect-modal",
                },
            },
        });
    },

    // The reconnect dialog (Components/Layout/ReconnectModal.razor). Blazor finds it by id, sets its
    // components-reconnect-* classes and raises components-reconnect-state-changed on it; this opens and
    // closes it and wires the buttons, as the .NET template's ReconnectModal.razor.js does.
    initReconnect: function () {
        const dialog = document.getElementById("components-reconnect-modal");
        if (!dialog) return;
        const retryWhenVisible = async () => { if (document.visibilityState === "visible") await retry(); };
        async function retry() {
            document.removeEventListener("visibilitychange", retryWhenVisible);
            try {
                if (!await Blazor.reconnect() && !await Blazor.resumeCircuit()) location.reload();
            } catch {
                document.addEventListener("visibilitychange", retryWhenVisible);
            }
        }
        // Attempt numbers as the user sees them: Blazor first retries about ten times without waiting, before the
        // "Still trying" line appears, so number from the first attempt shown ("1 of 20", not "11 of 30").
        let firstShown = null;
        const showAttempt = (current) => {
            firstShown ??= current;
            const max = Number(document.getElementById("components-reconnect-max-retries")?.innerText) || current;
            dialog.querySelector(".esm-reconnect-attempt").innerText = String(current - firstShown + 1);
            dialog.querySelector(".esm-reconnect-attempts").innerText = String(Math.max(1, max - firstShown + 1));
        };
        // A dropped connection that comes straight back (a phone returning to the tab, a Wi-Fi blip) shouldn't flash
        // the dialog: it only opens if reconnecting takes longer than this.
        let showTimer = null;
        const open = () => { clearTimeout(showTimer); showTimer = null; if (!dialog.open) dialog.showModal(); };
        dialog.addEventListener("components-reconnect-state-changed", (e) => {
            const state = e.detail.state;
            if (state === "retrying" && e.detail.secondsToNextAttempt > 0) showAttempt(e.detail.currentAttempt);
            if (state === "hide" || state === "failed") firstShown = null;
            if (state === "show") showTimer ??= setTimeout(open, esm.reconnectGraceMs);
            else if (state === "paused" || state === "failed" || state === "resume-failed") open();
            if (state === "hide") { clearTimeout(showTimer); showTimer = null; dialog.close(); }
            else if (state === "failed") document.addEventListener("visibilitychange", retryWhenVisible);
            else if (state === "rejected") location.reload();
        });
        // Back on the page (a phone unlocked, the tab shown again) while reconnecting: try now instead of waiting
        // out the current gap between attempts.
        document.addEventListener("visibilitychange", () => {
            // Only a nudge: Blazor keeps its own schedule if this attempt fails too.
            if (document.visibilityState === "visible" && dialog.classList.contains("components-reconnect-retrying"))
                Blazor.reconnect().catch(() => { });
        });
        dialog.addEventListener("cancel", (e) => e.preventDefault()); // Escape can't dismiss it
        document.getElementById("components-reconnect-button")?.addEventListener("click", retry);
        document.getElementById("components-resume-button")?.addEventListener("click", async () => {
            try {
                if (!await Blazor.resumeCircuit()) location.reload();
            } catch {
                dialog.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
            }
        });
        dialog.querySelector(".esm-reconnect-reload")?.addEventListener("click", () => location.reload());
        // Each shop icon gets its shop's accent ring, as on the listings page: the color picked on the Shops page,
        // else the one read from its profile image.
        for (const img of dialog.querySelectorAll(".esm-reconnect-mark img")) {
            const mark = img.parentElement;
            if (mark.dataset.accent) mark.style.setProperty("--shop", mark.dataset.accent);
            else esm.dominantColor(img.src).then((c) => c && mark.style.setProperty("--shop", c));
        }
    },

    // Marks each swipeable tab row (app.css, phones and tablets) with which ends have more tabs past them, so a dark
    // fade shows over that side. Checked on any scroll, resize or page change, at most once a frame.
    initTabEdges: function () {
        let queued = false;
        const update = () => {
            queued = false;
            for (const content of document.querySelectorAll(".mud-tabs-tabbar-content")) {
                const row = content.parentElement;
                const max = content.scrollWidth - content.clientWidth;
                row.toggleAttribute("data-esm-more-start", content.scrollLeft > 1);
                row.toggleAttribute("data-esm-more-end", content.scrollLeft < max - 1);
            }
        };
        const queue = () => { if (!queued) { queued = true; requestAnimationFrame(update); } };
        document.addEventListener("scroll", (e) => { if (e.target.classList?.contains("mud-tabs-tabbar-content")) queue(); }, true);
        window.addEventListener("resize", queue);
        new MutationObserver(queue).observe(document.body, { childList: true, subtree: true });
        queue();
    },

    // Clipboard API requires a secure context (HTTPS via HAProxy, or localhost).
    copy: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },

    // Remembers where the cursor (or selection) is in the text area inside the element with this id, on every change,
    // so a button tap that takes the focus away (phones blur the field first) still knows where to insert.
    trackSelection: function (containerId) {
        const el = document.querySelector("#" + containerId + " textarea, #" + containerId + " input");
        if (!el || el.dataset.esmTracked) return;
        el.dataset.esmTracked = "1";
        const save = () => { el.dataset.selStart = el.selectionStart; el.dataset.selEnd = el.selectionEnd; };
        ["select", "keyup", "click", "input", "focus", "blur", "touchend", "selectionchange"].forEach(e => el.addEventListener(e, save));
        document.addEventListener("selectionchange", () => { if (document.activeElement === el) save(); });
    },

    // Inserts text at the remembered cursor (replacing a selection; at the end if the field was never used), puts the
    // cursor after it and returns the new value, which the page stores (the field then shows the same text).
    insertAtSelection: function (containerId, text) {
        const el = document.querySelector("#" + containerId + " textarea, #" + containerId + " input");
        if (!el) return null;
        const value = el.value ?? "";
        const start = Math.min(value.length, Number(el.dataset.selStart ?? value.length));
        const end = Math.max(start, Math.min(value.length, Number(el.dataset.selEnd ?? start)));
        const result = value.slice(0, start) + text + value.slice(end);
        el.value = result;
        const caret = start + text.length;
        el.dataset.selStart = el.dataset.selEnd = String(caret);
        try { el.focus({ preventScroll: true }); el.setSelectionRange(caret, caret); } catch { }
        return result;
    },

    // Per-browser UI preferences (e.g. grid or list view). Storage can be blocked, so failures are ignored.
    getPref: function (key) {
        try { return localStorage.getItem("esm." + key); } catch { return null; }
    },
    setPref: function (key, value) {
        try { localStorage.setItem("esm." + key, value); } catch { }
    },

    // Opens the print dialog for a same-origin printout page (pick list, packing slips) without leaving this page: the
    // page loads in a hidden frame and, once its images and fonts are in, the frame prints itself. The previous frame
    // is removed first.
    printPage: function (url) {
        document.querySelectorAll("iframe.esm-print-frame").forEach(f => f.remove());
        const frame = document.createElement("iframe");
        frame.className = "esm-print-frame";
        frame.setAttribute("aria-hidden", "true");
        frame.tabIndex = -1;
        frame.onload = async () => {
            const win = frame.contentWindow;
            const doc = win.document;
            try {
                await Promise.all([...doc.images].map(img => img.complete ? null
                    : new Promise(done => { img.onload = img.onerror = done; })));
                if (doc.fonts) await doc.fonts.ready;
            } catch { }
            win.focus();
            win.print();
        };
        frame.src = url;
        document.body.appendChild(frame);
    },

    // The dominant colour of a same-origin image, as "#rrggbb", or null. Used to give each shop an accent colour
    // from its profile image. Near-white/near-black background pixels are skipped and the most common hue wins
    // (weighted towards saturated pixels); the result is clamped to a mid lightness so it reads on light and dark.
    dominantColor: async function (url) {
        try {
            const img = new Image();
            img.src = url;
            await img.decode();
            const size = 48;
            const canvas = document.createElement("canvas");
            canvas.width = canvas.height = size;
            const ctx = canvas.getContext("2d", { willReadFrequently: true });
            ctx.drawImage(img, 0, 0, size, size);
            const data = ctx.getImageData(0, 0, size, size).data;

            const bins = Array.from({ length: 36 }, () => ({ w: 0, r: 0, g: 0, b: 0 }));
            const grey = { w: 0, r: 0, g: 0, b: 0 };
            for (let i = 0; i < data.length; i += 4) {
                const r = data[i], g = data[i + 1], b = data[i + 2];
                if (data[i + 3] < 128) continue;
                const max = Math.max(r, g, b), min = Math.min(r, g, b);
                const l = (max + min) / 510;
                if (l < 0.08 || l > 0.94) continue;
                const s = max === min ? 0 : (max - min) / (255 - Math.abs(max + min - 255));
                if (s < 0.2) { grey.w++; grey.r += r; grey.g += g; grey.b += b; continue; }
                const d = max - min;
                let h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
                h = (h * 60 + 360) % 360;
                const w = s * (1 - Math.abs(l - 0.5));
                const bin = bins[Math.floor(h / 10) % 36];
                bin.w += w; bin.r += r * w; bin.g += g * w; bin.b += b * w;
            }

            let best = -1, bestScore = 0;
            for (let i = 0; i < 36; i++) {
                const score = bins[(i + 35) % 36].w + bins[i].w + bins[(i + 1) % 36].w;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            let rgb;
            if (best >= 0 && bestScore > 4) {
                const acc = { w: 0, r: 0, g: 0, b: 0 };
                for (const k of [(best + 35) % 36, best, (best + 1) % 36]) {
                    acc.w += bins[k].w; acc.r += bins[k].r; acc.g += bins[k].g; acc.b += bins[k].b;
                }
                rgb = [acc.r / acc.w, acc.g / acc.w, acc.b / acc.w];
            } else if (grey.w > 0) {
                rgb = [grey.r / grey.w, grey.g / grey.w, grey.b / grey.w];
            } else {
                return null;
            }
            return esm._midTone(rgb);
        } catch {
            return null;
        }
    },

    _midTone: function ([r, g, b]) {
        r /= 255; g /= 255; b /= 255;
        const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
        let h = 0, s = 0, l = (max + min) / 2;
        if (d > 0) {
            s = d / (1 - Math.abs(2 * l - 1));
            h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
            h = (h * 60 + 360) % 360;
        }
        l = Math.min(0.6, Math.max(0.42, l));
        if (s > 0.15) s = Math.max(s, 0.45);
        const c = (1 - Math.abs(2 * l - 1)) * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = l - c / 2;
        const [r1, g1, b1] = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x]
            : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x];
        return "#" + [r1, g1, b1].map(v => Math.round((v + m) * 255).toString(16).padStart(2, "0")).join("");
    },

    // Fills the <ColorBox> placeholders on the Orders page using the Home Assistant card's coloring engine
    // (window.EtsyOrderColors, from /js/etsy-orders-card.js) and the app's color list, so the page and the card color
    // the same text the same way. Only boxes whose text or list changed are redrawn.
    colorOrders: function (config) {
        const Engine = window.EtsyOrderColors;
        if (Engine && !document.getElementById("esm-order-colors")) {
            const style = document.createElement("style");
            style.id = "esm-order-colors";
            style.textContent = Engine.CSS;
            document.head.appendChild(style);
        }
        const key = (config && config.version) || "";
        if (Engine && esm._orderColorsKey !== key) {
            esm._orderColors = new Engine(config || {}, "var(--esm-paper)");
            esm._orderColorsKey = key;
        }
        for (const el of document.querySelectorAll(".esm-colorbox[data-color-text]")) {
            const { colorText: text, colorClass: cls, colorSeed: seed, colorSmall: small } = el.dataset;
            const sig = `${key}|${cls}|${seed}|${text}`;
            if (el.dataset.colored === sig) continue;
            const box = Engine ? esm._orderColors.box(text, seed, small === "true") : { cls: "", style: "", inner: "" };
            const inner = document.createElement("span");
            inner.className = cls + box.cls;
            if (box.style) inner.style.cssText = box.style;
            inner.innerHTML = box.inner; // the engine's own markup (lava blobs); the text goes in as text
            inner.append(text);
            el.replaceChildren(inner);
            el.dataset.colored = sig;
        }
    }
};

esm.initReconnect();
esm.initTabEdges();
esm.startBlazor();
