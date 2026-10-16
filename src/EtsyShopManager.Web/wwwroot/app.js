window.esm = {
    // Clipboard API requires a secure context (HTTPS via HAProxy, or localhost).
    copy: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },

    // Per-browser UI preferences (e.g. grid or list view). Storage can be blocked, so failures are ignored.
    getPref: function (key) {
        try { return localStorage.getItem("esm." + key); } catch { return null; }
    },
    setPref: function (key, value) {
        try { localStorage.setItem("esm." + key, value); } catch { }
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
