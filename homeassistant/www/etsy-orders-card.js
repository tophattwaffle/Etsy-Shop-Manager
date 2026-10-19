// etsy-orders-card v2.9.0 — open Etsy orders as order cards, most urgent first.
//
// Reads the `open_orders` / `total_items_needed` attributes of Etsy open-order
// sensors. The color list (`colors`, `metallic`, `random_words`) comes from the
// Etsy Shop Manager app (Settings -> Order colors) through the sensors'
// `color_config` attribute; any of those set here are merged on top of it.
// The coloring engine (EtsyOrderColors) is shared with the app's Orders page,
// which loads this same file. Config:
//   shops:             list of { entity, name?, icon? (emoji or text),
//                      order_url? (desktop-only override of order_url),
//                      accent? ("#hex" outline for this shop's orders; default
//                      the shop's color from the app, none if it has none) }
//   android_package:   on Android, open orders in this app (an intent link
//                      with the web order page as fallback) instead of the
//                      per-shop desktop links. Omit to use plain web links.
//   show_prices:       show order totals and the shop total (default false)
//   show_make_list:    show the per-shop "To make" summary, less what is made (default true)
//   show_made:         a Made checkbox on each item, kept in sync with the app's Orders
//                      and To make pages (default true; needs integration 0.4.0 or later,
//                      hidden otherwise). An item of several units counts up one per tap
//                      ("1/3", "2/3", …) and clears after the last.
//   combine_shops:     one list of every shop's orders, most urgent first, with a
//                      shop picker at the top, like the app's Orders page: a pill
//                      showing the shop's icon (the app's storefront for All shops)
//                      that opens a menu of icons, full names and order counts;
//                      remembered per device. Under "All shops" each order names
//                      its shop. Give each shop an icon for the pill.
//                      (default false: a section per shop)
//   green_hours:       ship-by pills and the header's "Next" chip are fully
//                      green at this many hours or more to ship (default 72)
//   red_hours:         ...and fully red at this many hours or fewer, blending
//                      green → yellow → red in between; below it the whole
//                      order card is tinted red (default 12)
//   colors:            color names for tinting variation and personalization
//                      boxes, added to the app's list (same name: this wins), as { "Name": "#hex" } or
//                      { "Name": ["#hex", "#hex"] } for multi-color filaments.
//                      CSS color names (black, purple, dark green, …) are
//                      recognised without being listed; several colors in one
//                      box fade from one to the next in the order written.
//   color_tint:        strength (%) of that tint; 0 turns coloring off (default 35)
//   metallic:          color names (keys of `colors`, or CSS names) that get a
//                      metal effect over their part of a box: a bevel sheen and
//                      a glint of light sweeping across (default: the app's list,
//                      else ["Copper"])
//   metallic_shine:    strength (%) of that sheen and glint; 0 turns it off
//                      (default 60)
//   animate_metallic:  the glint sweeps across every few seconds (default true;
//                      paused and off like animate_random)
//   random_words:      words or phrases that mark a "random color" choice; such
//                      boxes get the random_style effect in colors drawn from
//                      `colors` (default: the app's list, else ["random"])
//   random_style:      "lava" (soft blobs drifting like a lava lamp) or
//                      "confetti" (falling dots) (default "lava")
//   random_min_brightness: 0–100; `colors` darker than this (by their brightest
//                      channel) are left out of the random effect, along with
//                      greys, muddy browns and near-whites (default 60)
//   animate_random:    the effect moves and the background and outline drift
//                      through their colors (default true; paused while the
//                      window is unfocused or hidden, off when the device asks
//                      for reduced motion)
//   order_url:         link for each order; "{receipt_id}" is substituted
//                      (default Etsy's sold-orders page). Set false to disable.
//                      A non-http scheme (e.g. "microsoft-edge:https://…")
//                      hands the link to another browser via the OS.
const esc = (s) =>
  String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const shortTitle = (t) => String(t || "").split("|")[0].trim();
// The app's brand mark (Material "storefront", as in its top bar), for "All shops" in the combined view's shop picker.
const STOREFRONT = `<svg class="storefront" viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M21.9,8.89l-1.05-4.37c-0.22-0.9-1-1.52-1.91-1.52H5.05C4.15,3,3.36,3.63,3.15,4.52L2.1,8.89 c-0.24,1.02-0.02,2.06,0.62,2.88C2.8,11.88,2.91,11.96,3,12.06V19c0,1.1,0.9,2,2,2h14c1.1,0,2-0.9,2-2v-6.94 c0.09-0.09,0.2-0.18,0.28-0.28C21.92,10.96,22.15,9.91,21.9,8.89z M18.91,4.99l1.05,4.37c0.1,0.42,0.01,0.84-0.25,1.17 C19.57,10.71,19.27,11,18.77,11c-0.61,0-1.14-0.49-1.21-1.14L16.98,5L18.91,4.99z M13,5h1.96l0.54,4.52 c0.05,0.39-0.07,0.78-0.33,1.07C14.95,10.85,14.63,11,14.22,11C13.55,11,13,10.41,13,9.69V5z M8.49,9.52L9.04,5H11v4.69 C11,10.41,10.45,11,9.71,11c-0.34,0-0.65-0.15-0.89-0.41C8.57,10.3,8.45,9.91,8.49,9.52z M4.04,9.36L5.05,5h1.97L6.44,9.86 C6.36,10.51,5.84,11,5.23,11c-0.49,0-0.8-0.29-0.93-0.47C4.03,10.21,3.94,9.78,4.04,9.36z M5,19v-6.03C5.08,12.98,5.15,13,5.23,13 c0.87,0,1.66-0.36,2.24-0.95c0.6,0.6,1.4,0.95,2.31,0.95c0.87,0,1.65-0.36,2.23-0.93c0.59,0.57,1.39,0.93,2.29,0.93 c0.84,0,1.64-0.35,2.24-0.95c0.58,0.59,1.37,0.95,2.24,0.95c0.08,0,0.15-0.02,0.23-0.03V19H5z"/></svg>`;
const isAndroid = () => typeof navigator !== "undefined" && /Android/i.test(navigator.userAgent || "");
const words = (s) => String(s || "").toLowerCase().match(/[a-z]+/g) || [];
const CSS_KEYWORDS = new Set(["inherit", "initial", "unset", "revert", "currentcolor", "transparent"]);
const isCssColor = (w) => /^[a-z]{3,}$/.test(w) && !CSS_KEYWORDS.has(w) && typeof CSS !== "undefined" && CSS.supports("color", w);
const RAINBOW = ["#e53935", "#fb8c00", "#fdd835", "#43a047", "#1e88e5", "#8e24aa"];
// Brightness (HSV value: the brightest channel), saturation and perceived lightness, each 0–1,
// of a #rrggbb color. Anything else (CSS names) passes as bright, vivid and mid-light.
const tone = (col) => {
  const m = /^#([0-9a-f]{6})$/i.exec(col);
  if (!m) return { v: 1, s: 1, luma: 0.5 };
  const n = parseInt(m[1], 16);
  const [r, g, b] = [n >> 16, (n >> 8) & 255, n & 255].map((x) => x / 255);
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  return { v: max, s: max ? (max - min) / max : 0, luma: 0.299 * r + 0.587 * g + 0.114 * b };
};
// Seeded PRNG, so a "random" box keeps the same pattern on every re-render.
const rng = (seed) => {
  let h = 2166136261;
  for (const ch of String(seed)) h = Math.imul(h ^ ch.charCodeAt(0), 16777619);
  return () => {
    h = Math.imul(h ^ (h >>> 15), 2246822507);
    h = Math.imul(h ^ (h >>> 13), 3266489909);
    return ((h ^= h >>> 16) >>> 0) / 4294967296;
  };
};

// The coloring engine: tints variation pills and personalization notes by the colors they name, with
// metal and "random" effects. Shared with the Etsy Shop Manager app (its Orders page loads this file and
// uses window.EtsyOrderColors), so both always color the same way. `base` is the background the tints are
// mixed with.
class EtsyOrderColors {
  static DEFAULTS = {
    colors: {},
    color_tint: 35,
    metallic: ["Copper"],
    metallic_shine: 60,
    animate_metallic: true,
    random_words: ["random"],
    random_style: "lava",
    random_min_brightness: 60,
    animate_random: true,
  };

  constructor(config, base = "var(--ha-card-background, var(--card-background-color, #fff))") {
    this.c = { ...EtsyOrderColors.DEFAULTS, ...config };
    this.base = base;
    // Color names are matched word by word, so "Dark  Green!" finds the key "dark green".
    this._colorMap = new Map(Object.entries(this.c.colors || {}).map(([k, v]) => [words(k).join(" "), [].concat(v).map(String)]));
    this._colorMaxWords = Math.max(3, ...[...this._colorMap.keys()].map((k) => k.split(" ").length));
    this._metallic = new Set([].concat(this.c.metallic || []).map((w) => words(w).join(" ")).filter(Boolean));
    // The random effects skip dark colors, dull greys and browns (both look muddy) and near-whites
    // (which vanish on a light theme); vivid colors stay even when they read darkish, like a deep red.
    const minV = Number(this.c.random_min_brightness) / 100;
    const palette = [...new Set([...this._colorMap.values()].flat())].filter((col) => {
      const { v, s, luma } = tone(col);
      return v >= minV && !(s < 0.3 && v < 0.8) && luma < 0.9;
    });
    this._palette = palette.length ? palette : RAINBOW;
    this._randomWords = [].concat(this.c.random_words || []).map((w) => words(w).join(" ")).filter(Boolean);
  }

  // The shared color list (from the app, via a sensor's `color_config` attribute) with the card's own
  // settings on top: `colors` entries are merged by name, other keys replace the shared ones.
  static merge(shared, own) {
    const out = { ...(shared || {}) };
    for (const [k, v] of Object.entries(own || {})) {
      out[k] = k === "colors" ? { ...(out.colors || {}), ...(v || {}) } : v;
    }
    return out;
  }

  // How to draw a box of `text`: { cls: extra classes ("" when uncolored), style, inner (HTML before the
  // text) }. A "random" choice gets the random effect, seeded by `seed` so it stays put between renders.
  box(text, seed, small) {
    const fx = this.isRandom(text) ? this.randomFx(`${seed}|${text}`, small) : null;
    if (fx) return { cls: ` colored random ${fx.kind}${this.c.animate_random ? "" : " still"}`, style: fx.style, inner: fx.inner };
    const groups = this.colorsIn(text);
    const metal = this.metalStyle(groups);
    const style = this.colorStyle(groups) + (metal ? ` ${metal}` : "");
    if (!style) return { cls: "", style: "", inner: "" };
    const multi = groups.some((g) => g.length > 1) ? " multi" : "";
    return { cls: ` colored${multi}${metal ? ` metal${this.c.animate_metallic ? "" : " still"}` : ""}`, style, inner: "" };
  }

  // Colors named in free text, in the order written: the `colors` map first
  // (longest name wins), then CSS color names, joining words ("Dark Green" → darkgreen).
  // Each name is one group, so a multi-color filament stays together: [["black"], ["#4fc3f7", "#ff1fd1"]].
  // A group named in `metallic` carries metal: true.
  colorsIn(text) {
    const w = words(text);
    const out = [];
    for (let i = 0; i < w.length; ) {
      let used = 1;
      for (let n = Math.min(this._colorMaxWords, w.length - i); n > 0; n--) {
        const parts = w.slice(i, i + n);
        const cols = this._colorMap.get(parts.join(" ")) || (isCssColor(parts.join("")) ? [parts.join("")] : null);
        if (cols) {
          const hit = Object.assign([...cols], { metal: this._metallic.has(parts.join(" ")) });
          if (out[out.length - 1]?.join() !== hit.join()) out.push(hit);
          used = n;
          break;
        }
      }
      i += used;
    }
    return out;
  }

  // A tinted fill fading through the colors, with a full-strength outline in the same colors.
  // Each named color gets an equal share of the width, with its stop at the middle of its share so
  // neighbours fade into each other. A multi-color filament stacks its colors top to bottom over its
  // share instead (see multiStyle): "Black base, Neon Rider pattern" is half black, then light blue over
  // magenta. The fill is mixed with the background so it stays opaque over the outline layer.
  colorStyle(groups) {
    const t = Number(this.c.color_tint);
    if (!groups.length || !(t > 0)) return "";
    // In the left-to-right blend a filament counts as the mix of its top and bottom colors.
    const stops = groups.map((g, gi) => [g.length > 1 ? `color-mix(in srgb, ${g[0]}, ${g[g.length - 1]})` : g[0], ((gi + 0.5) / groups.length) * 100]);
    if (stops.length === 1) stops.splice(0, 1, [stops[0][0], 0], [stops[0][0], 100]);
    const grad = (fn) => `linear-gradient(90deg, ${stops.map(([col, p]) => `${fn(col)} ${p.toFixed(1)}%`).join(", ")})`;
    const tint = (col) => `color-mix(in srgb, ${col} ${Math.min(100, t)}%, ${this.base})`;
    return `background: ${grad(tint)} padding-box, ${grad((col) => col)} border-box; border-color: transparent;${this.multiStyle(groups, tint)}`;
  }

  // The stacked layer (.multi::before) for multi-color filaments: each one's colors run top to bottom over
  // its share of the box, tinted inside and full strength on the outline, fading in from its neighbours at
  // the sides. "" when no group has more than one color.
  multiStyle(groups, tint) {
    if (!groups.some((g) => g.length > 1)) return "";
    const share = 100 / groups.length;
    // Background-position percentages are of (box - layer), so this puts the layer's left edge at gi * share.
    const at = (gi) => `${(share >= 100 ? 0 : ((gi * share) / (100 - share)) * 100).toFixed(1)}% 0 / ${share.toFixed(1)}% 100% no-repeat`;
    const multis = groups.map((g, gi) => [g, gi]).filter(([g]) => g.length > 1);
    const layer = (fn, box) => multis.map(([g, gi]) => `linear-gradient(180deg, ${g.map(fn).join(", ")}) ${at(gi)} ${box}`);
    return ` --multi: ${[...layer(tint, "padding-box"), ...layer((col) => col, "border-box")].join(", ")}; --multi-mask: ${this.shareMask(groups, (g) => g.length > 1)};`;
  }

  // A left-to-right mask that is opaque over the shares of the groups `pick` selects and transparent
  // elsewhere, fading where a picked share meets another color (not at the box's own edges); "none"
  // when every group is picked.
  shareMask(groups, pick) {
    if (groups.every(pick)) return "none";
    const share = 100 / groups.length;
    const fade = share / 4;
    const stops = groups.flatMap((g, gi) => {
      if (!pick(g)) return [];
      const [a, b] = [gi * share, (gi + 1) * share];
      const from = a > 0 ? [`transparent ${a.toFixed(1)}%`, `#000 ${(a + fade).toFixed(1)}%`] : ["#000 0%"];
      const to = b < 100 ? [`#000 ${(b - fade).toFixed(1)}%`, `transparent ${b.toFixed(1)}%`] : ["#000 100%"];
      return [...from, ...to];
    });
    return `linear-gradient(90deg, ${stops.join(", ")})`;
  }

  // The metal layer (.metal::after) over the metallic groups' shares of the box, or "" when none.
  // A mask fades it in and out at the edges of those shares, so in "Black & Copper" only the
  // copper half shines; when every group is metallic the whole box does.
  metalStyle(groups) {
    const shine = Number(this.c.metallic_shine);
    if (!(shine > 0) || !(Number(this.c.color_tint) > 0) || !groups.some((g) => g.metal)) return "";
    return `--shine: ${Math.min(100, shine) / 100}; --metal-mask: ${this.shareMask(groups, (g) => g.metal)};`;
  }

  isRandom(text) {
    const s = ` ${words(text).join(" ")} `;
    return this._randomWords.some((w) => s.includes(` ${w} `));
  }

  // A "random" box: colors drawn from the palette. Behind the effect is a slow drift through tinted
  // palette colors (mixed with the background, so opaque and readable), and around it a
  // multicolor outline. Both are horizontal tiles sized in pixels rather than box widths, so a small
  // pill and a wide note show the same colors per inch; each tile ends on its first color, so sliding
  // it a whole tile loops seamlessly. Returns { kind, style, inner } or null when coloring is off.
  randomFx(seed, small) {
    const t = Number(this.c.color_tint);
    if (!(t > 0)) return null;
    const r = rng(seed);
    const pick = () => this._palette[Math.floor(r() * this._palette.length)];
    const loop = (cols) => [...cols, cols[0]].join(", ");
    const wash = Array.from({ length: 4 }, pick).map((col) => `color-mix(in srgb, ${col} ${Math.min(100, t + 10)}%, ${this.base})`);
    const ring = Array.from({ length: 5 }, pick);
    const frame = `background: linear-gradient(90deg, ${loop(wash)}) padding-box, linear-gradient(90deg, ${loop(ring)}) border-box; background-size: 480px 100%, 240px 100%; border-color: transparent;`;

    if (this.c.random_style === "confetti") {
      // Dots go in --confetti for the .confetti::before layer, which tiles them vertically so they
      // can fall in a seamless loop (dots stay off the tile edges so none pop at the seam).
      const [dots, maxSize] = small ? [9, 1.8] : [27, 3];
      const strength = Math.min(100, t + 40);
      const confetti = Array.from({ length: dots }, () => {
        const size = maxSize / 2 + (r() * maxSize) / 2;
        const at = `${Math.round(r() * 100)}% ${Math.round(10 + r() * 80)}%`;
        return `radial-gradient(circle at ${at}, color-mix(in srgb, ${pick()} ${strength}%, transparent) ${size.toFixed(1)}px, transparent ${(size + 0.8).toFixed(1)}px)`;
      });
      return { kind: "confetti", style: `--confetti: ${confetti.join(", ")}; ${frame}`, inner: "" };
    }

    // Lava lamp: a few soft blobs spread across the box, each drifting and swelling on its own
    // path and pace. The soft edges come from the gradient itself, so the motion is transform-only.
    const n = small ? 3 : 4;
    const strength = Math.min(100, t + 30);
    const blobs = Array.from({ length: n }, (_, k) => {
      const x = Math.round(((k + 0.2 + r() * 0.6) / n) * 100);
      const y = Math.round(25 + r() * 50);
      const d = [10, 12, 15, 20][Math.floor(r() * 4)];
      const style = `left: ${x}%; top: ${y}%; --c: color-mix(in srgb, ${pick()} ${strength}%, transparent); --d: ${d}s; --k: lava-${(k % 4) + 1}`;
      return `<i class="blob" style="${esc(style)}"></i>`;
    });
    return { kind: "lava", style: frame, inner: blobs.join("") };
  }

  // The styles behind .colored, .random and .metal boxes (pills use .var, notes .note). Animations use
  // var(--phase) so a re-render continues them where they were.
  static CSS = `
        .var.colored { color: var(--primary-text-color); font-weight: 500; }
        .note.colored { border-width: 1px 1px 1px 4px; border-style: solid; }
        /* Multi-color filaments: their colors stacked top to bottom (--multi), over the base blend and under the
           text, covering the outline too; --multi-mask limits them to their share of the box. */
        .multi { position: relative; isolation: isolate; }
        .multi::before { content: ""; position: absolute; inset: -1px; z-index: -1; pointer-events: none; border-radius: inherit;
                         border: 1px solid transparent; background: var(--multi);
                         -webkit-mask-image: var(--multi-mask); mask-image: var(--multi-mask); }
        .note.multi::before { inset: -1px -1px -1px -4px; border-width: 1px 1px 1px 4px; }
        .random { position: relative; overflow: hidden; isolation: isolate; animation: random-drift 48s linear infinite var(--phase, 0ms); }
        .confetti::before { content: ""; position: absolute; left: 0; right: 0; top: -100%; height: 200%; z-index: -1; pointer-events: none;
                            background: var(--confetti); background-size: 100% 50%; animation: confetti-fall 4s linear infinite var(--phase, 0ms); }
        .blob { position: absolute; z-index: -1; pointer-events: none; width: 70%; height: 300%; translate: -50% -50%; border-radius: 50%;
                background: radial-gradient(closest-side, var(--c) 25%, transparent);
                animation: var(--k) var(--d) ease-in-out infinite alternate var(--phase, 0ms); }
        .random.still, .random.still::before, .random.still .blob { animation: none; }
        @keyframes confetti-fall { to { transform: translateY(50%); } }
        /* Per cycle the background moves one 480px tile and the outline two 240px tiles. */
        @keyframes random-drift { to { background-position: 480px 0, 480px 0; } }
        @keyframes lava-1 { from { transform: translate(-40%, -15%) scale(0.9); } to { transform: translate(45%, 20%) scale(1.25); } }
        @keyframes lava-2 { from { transform: translate(35%, 20%) scale(1.2); } to { transform: translate(-45%, -20%) scale(0.85); } }
        @keyframes lava-3 { from { transform: translate(-20%, 25%) scale(1); } to { transform: translate(30%, -25%) scale(1.3); } }
        @keyframes lava-4 { from { transform: translate(25%, -25%) scale(1.15); } to { transform: translate(-35%, 15%) scale(0.9); } }
        /* Metal: a bevel (light top edge, darker band below the middle) and a glint that sweeps
           left to right in the first third of each cycle. The layer covers the border too and sits
           under the text; the glint is a 300%-wide tile, off the box at either end position. */
        .metal { position: relative; isolation: isolate; }
        .metal::after { content: ""; position: absolute; inset: -1px; z-index: -1; pointer-events: none; border-radius: inherit;
                        background: linear-gradient(105deg, transparent 43%, rgba(255, 255, 255, calc(var(--shine) * 0.95)) 50%, transparent 57%) 100% 0 / 300% 100% no-repeat,
                                    linear-gradient(180deg, rgba(255, 255, 255, calc(var(--shine) * 0.5)), rgba(255, 255, 255, 0) 42%,
                                                    rgba(0, 0, 0, calc(var(--shine) * 0.22)) 58%, rgba(255, 255, 255, calc(var(--shine) * 0.18)));
                        -webkit-mask-image: var(--metal-mask); mask-image: var(--metal-mask);
                        animation: metal-glint 6s ease-in-out infinite var(--phase, 0ms); }
        .note.metal::after { inset: -1px -1px -1px -4px; }
        .metal.still::after { animation: none; }
        @keyframes metal-glint { 0% { background-position: 100% 0, 0 0; } 33%, 100% { background-position: 0% 0, 0 0; } }`;
}
window.EtsyOrderColors = EtsyOrderColors;

class EtsyOrdersCard extends HTMLElement {
  setConfig(config) {
    if (!Array.isArray(config.shops) || !config.shops.length) {
      throw new Error("etsy-orders-card: 'shops' must be a non-empty list");
    }
    this._config = {
      show_prices: false,
      show_make_list: true,
      show_made: true,
      combine_shops: false,
      green_hours: 72,
      red_hours: 12,
      order_url: "https://www.etsy.com/your/orders/sold?order_id={receipt_id}",
      ...config,
      shops: config.shops.map((s) => (typeof s === "string" ? { entity: s } : s)),
    };
    this._own = config;
    this._selectedShop = this._loadSelectedShop();
    this._colors = null;
    this._signature = null;
    if (this._hass) this._render();
  }

  set hass(hass) {
    this._hass = hass;
    const shared = this._sharedColors(hass);
    const colorsSig = JSON.stringify(shared?.version ?? shared ?? null);
    if (!this._colors || colorsSig !== this._colorsSig) {
      this._colorsSig = colorsSig;
      this._colors = new EtsyOrderColors(EtsyOrderColors.merge(shared, this._own));
      this._signature = null;
    }
    this._shopAccents = shared?.shop_accents || {};
    const sig = this._config.shops.map((s) => hass.states[s.entity]?.last_updated).join("|");
    if (sig !== this._signature) {
      this._signature = sig;
      this._render();
    }
  }

  // The app's color list, from the first shop sensor that carries it.
  _sharedColors(hass) {
    for (const s of this._config.shops) {
      const cc = hass.states[s.entity]?.attributes?.color_config;
      if (cc && typeof cc === "object") return cc;
    }
    return null;
  }

  getCardSize() {
    return 8;
  }

  getGridOptions() {
    return { columns: 12, rows: "auto" };
  }

  connectedCallback() {
    // Countdowns are relative to now, so refresh them every minute.
    this._timer = setInterval(() => this._render(), 60000);
    // Animations (random and metal alike) only move while the window has focus; pausing (not stopping)
    // resumes them in place. The "paused" class on ha-card is the single switch the CSS uses for all of them.
    // document.hasFocus() reads false in the Companion app's WebView until something is tapped,
    // so it only decides the starting state on mouse-driven (desktop) devices; touch devices
    // start unpaused and rely on blur/focus events and visibility.
    this._onWindowFocus = (e) => {
      this._blurred = e.type === "blur";
      this._updatePaused();
    };
    this._onVisibility = () => {
      // Coming back to the app or tab doesn't always send a focus event, so visible counts as focused.
      if (!document.hidden) this._blurred = false;
      this._updatePaused();
    };
    const desktop = window.matchMedia?.("(hover: hover) and (pointer: fine)").matches;
    this._blurred = desktop ? !document.hasFocus() : false;
    window.addEventListener("focus", this._onWindowFocus);
    window.addEventListener("blur", this._onWindowFocus);
    // A tap or click means the page is in use, even where the WebView sends no focus event.
    window.addEventListener("pointerdown", this._onWindowFocus);
    // A tap anywhere outside the card closes its shop menu.
    this._onOutside = (e) => {
      if (this._menuOpen && !e.composedPath().includes(this)) {
        this._menuOpen = false;
        this._render();
      }
    };
    document.addEventListener("pointerdown", this._onOutside);
    document.addEventListener("visibilitychange", this._onVisibility);
    this._updatePaused();
  }

  disconnectedCallback() {
    clearInterval(this._timer);
    window.removeEventListener("focus", this._onWindowFocus);
    window.removeEventListener("blur", this._onWindowFocus);
    window.removeEventListener("pointerdown", this._onWindowFocus);
    document.removeEventListener("pointerdown", this._onOutside);
    document.removeEventListener("visibilitychange", this._onVisibility);
  }

  _updatePaused() {
    const paused = document.hidden || this._blurred;
    if (paused && !this._pausedAt) this._pausedAt = Date.now();
    if (!paused && this._pausedAt) {
      this._pausedTotal = (this._pausedTotal || 0) + Date.now() - this._pausedAt;
      this._pausedAt = null;
    }
    this._paused = paused;
    this.shadowRoot?.querySelector("ha-card")?.classList.toggle("paused", paused);
  }

  // Animation clock: wall time minus time spent paused. Each re-render starts the animation at
  // this point, so it carries on where it was instead of jumping to wall-clock time.
  _animClock() {
    return Date.now() - (this._pausedTotal || 0) - (this._pausedAt ? Date.now() - this._pausedAt : 0);
  }

  _due(ts) {
    if (!ts) return { text: "—", cls: "", style: "", urgent: false };
    const c = this._config;
    const secs = ts - Date.now() / 1000;
    const abs = Math.abs(secs);
    const d = Math.floor(abs / 86400);
    const h = Math.floor((abs % 86400) / 3600);
    const m = Math.floor((abs % 3600) / 60);
    const span = d > 0 ? `${d}d ${h}h` : h > 0 ? `${h}h ${m}m` : `${m}m`;
    // Hue 120 (green) at green_hours or more, 0 (red) at red_hours or fewer, linear in between.
    const hrs = secs / 3600;
    const f = Math.min(1, Math.max(0, (hrs - c.red_hours) / Math.max(0.01, c.green_hours - c.red_hours)));
    const hue = Math.round(120 * f);
    const style = `background: hsla(${hue}, 75%, 48%, 0.3); border-color: hsla(${hue}, 75%, 42%, 0.8);`;
    const urgent = hrs < c.red_hours;
    if (secs < 0) return { text: `${span} late`, cls: "late", style, urgent };
    return { text: span, cls: "", style, urgent };
  }

  _money(amount, currency) {
    try {
      return new Intl.NumberFormat(undefined, { style: "currency", currency: currency || "USD" }).format(amount);
    } catch (e) {
      return `${amount} ${currency || ""}`.trim();
    }
  }

  // A shop's sensor state, display name and icon.
  _shopInfo(shop) {
    const st = this._hass.states[shop.entity];
    return {
      st,
      name: shop.name || st?.attributes.friendly_name || shop.entity,
      icon: shop.icon ? `<span class="shop-icon">${esc(shop.icon)}</span>` : "",
    };
  }

  // A shop's open orders, each with what drawing it needs: the shop's order link, its accent color and name.
  _entries(shop) {
    const { st, name } = this._shopInfo(shop);
    if (!st) return [];
    const orderUrl = this._orderUrlTemplate(shop);
    // The shop's color from the app (keyed by Etsy shop id), unless the card sets its own.
    const accent = shop.accent || this._shopAccents?.[String(st.attributes.shop_id)];
    return (st.attributes.open_orders || []).map((o) => ({ o, orderUrl, accent, shopName: name }));
  }

  // Soonest ship-by first, orders without one last (as the app's Orders page sorts).
  _byShipBy(entries) {
    return [...entries].sort((a, b) => (a.o.ship_by_ts || Infinity) - (b.o.ship_by_ts || Infinity));
  }

  // The header chips (orders, items and made, next ship-by, total) and the "To make" line for a set of orders.
  _summary(entries, items) {
    const c = this._config;
    const orders = entries.map((e) => e.o);
    const made = orders.reduce((n, o) => n + (o.items || []).reduce((k, i) => k + this._made(o, i), 0), 0);
    const chips = [
      `<span class="chip">${orders.length} order${orders.length === 1 ? "" : "s"}</span>`,
      `<span class="chip">${items} item${items === 1 ? "" : "s"}${made ? ` · ${made} made` : ""}</span>`,
    ];
    if (orders.length) {
      const next = this._due(orders[0].ship_by_ts);
      chips.push(`<span class="chip next ${next.cls}" style="${next.style}">Next: ${esc(next.text)}</span>`);
    }
    if (c.show_prices && orders.length) {
      const total = orders.reduce((n, o) => n + (o.order_total || 0), 0);
      chips.push(`<span class="chip">${esc(this._money(total, orders[0].currency_code))}</span>`);
    }

    let makeList = "";
    if (c.show_make_list && orders.length) {
      const counts = new Map();
      // What is left: units marked made are taken off, and a title with nothing left drops out.
      for (const o of orders) for (const i of o.items || []) {
        const t = shortTitle(i.title);
        counts.set(t, (counts.get(t) || 0) + this._units(i) - this._made(o, i));
      }
      const left = [...counts].filter(([, n]) => n > 0);
      makeList = `<div class="make"><span class="make-label">To make</span>${left.length
        ? left.sort((a, b) => b[1] - a[1]).map(([t, n]) => `<span class="make-item">${esc(t)} <b>×${n}</b></span>`).join("")
        : `<span class="make-done">All made ✓</span>`}</div>`;
    }
    return { chips: `<span class="chips">${chips.join("")}</span>`, makeList };
  }

  _itemCount(st, orders) {
    return st.attributes.total_items_needed ?? orders.reduce((n, o) => n + (o.items || []).reduce((k, i) => k + (i.quantity || 1), 0), 0);
  }

  _orders(entries, showShop) {
    return entries.length
      ? entries.map((e) => this._order(e.o, e.orderUrl, e.accent, showShop ? e.shopName : null)).join("")
      : `<div class="empty">🎉 No open orders — nothing to ship.</div>`;
  }

  // One section per shop (the default layout).
  _shop(shop) {
    const { st, name, icon } = this._shopInfo(shop);
    if (!st) return `<section class="shop"><div class="shop-head">${icon}<span class="shop-name">${esc(name)}</span></div><div class="empty">Entity not found: ${esc(shop.entity)}</div></section>`;
    const entries = this._byShipBy(this._entries(shop));
    const { chips, makeList } = this._summary(entries, this._itemCount(st, entries.map((e) => e.o)));
    return `<section class="shop">
      <div class="shop-head">${icon}<span class="shop-name">${esc(name)}</span>${chips}</div>
      ${makeList}${this._orders(entries, false)}</section>`;
  }

  // combine_shops: every shop's orders in one list, most urgent first, with a shop picker on top (like the shop list on
  // the app's Orders page). The picker is a pill like the chips beside it, showing only the shop's icon (the app's
  // storefront mark for All shops); it opens a menu of icons and full names. "All shops" names each order's shop; the
  // choice is kept per device.
  _combined() {
    const shops = this._config.shops;
    const selected = shops.some((s) => s.entity === this._selectedShop) ? this._selectedShop : "";
    const perShop = shops.map((s) => ({ s, ...this._shopInfo(s), entries: this._entries(s) }));
    const shown = perShop.filter((p) => !selected || p.s.entity === selected);
    const entries = this._byShipBy(shown.flatMap((p) => p.entries));
    const items = shown.reduce((n, p) => n + (p.st ? this._itemCount(p.st, p.entries.map((e) => e.o)) : 0), 0);
    const { chips, makeList } = this._summary(entries, items);
    const total = perShop.reduce((n, p) => n + p.entries.length, 0);
    const current = perShop.find((p) => p.s.entity === selected);
    const choices = [{ value: "", mark: STOREFRONT, name: "All shops", count: total }].concat(
      perShop.map((p) => ({ value: p.s.entity, mark: p.s.icon ? esc(p.s.icon) : "", name: p.name, count: p.st ? p.entries.length : null })),
    );
    // A shop without an icon shows its name on the pill instead.
    const pillMark = current ? (current.s.icon ? esc(current.s.icon) : esc(current.name)) : STOREFRONT;
    const menu = this._menuOpen
      ? `<div class="shop-menu" role="listbox" aria-label="Shop">${choices.map((c) => `<button type="button" class="shop-option${c.value === selected ? " on" : ""}"
          role="option" aria-selected="${c.value === selected}" data-shop="${esc(c.value)}"><span class="mark">${c.mark}</span><span class="name">${esc(c.name)}</span><span class="count">${c.count ?? "not found"}</span></button>`).join("")}</div>`
      : "";
    const missing = shown.filter((p) => !p.st).map((p) => `<div class="empty">Entity not found: ${esc(p.s.entity)}</div>`).join("");
    return `<section class="shop">
      <div class="shop-head"><span class="shop-pick-wrap"><button type="button" class="chip shop-pick${this._menuOpen ? " open" : ""}"
        aria-haspopup="listbox" aria-expanded="${this._menuOpen ? "true" : "false"}" aria-label="Shop: ${esc(current ? current.name : "All shops")}"
        title="${esc(current ? current.name : "All shops")}">${pillMark}</button>${menu}</span>${chips}</div>
      ${makeList}${missing}${missing && !entries.length ? "" : this._orders(entries, !selected && shops.length > 1)}</section>`;
  }

  // The device's last shop choice in the combined view (per card, by its shop list); none if storage is off.
  _storageKey() {
    return `etsy-orders-card:shop:${this._config.shops.map((s) => s.entity).join(",")}`;
  }

  _loadSelectedShop() {
    try {
      return localStorage.getItem(this._storageKey()) || "";
    } catch (e) {
      return "";
    }
  }

  // The shop picker: the pill opens and closes the menu, a menu entry picks that shop.
  _onShopClick(path) {
    if (path.find((el) => el.classList?.contains("shop-pick"))) {
      this._setMenu(!this._menuOpen);
      return true;
    }
    const option = path.find((el) => el.classList?.contains("shop-option"));
    if (!option) return false;
    this._selectedShop = option.dataset.shop;
    try {
      localStorage.setItem(this._storageKey(), option.dataset.shop);
    } catch (err) {
      // Private mode or storage off: the choice lasts until the page reloads.
    }
    this._setMenu(false);
    return true;
  }

  // Opening focuses the chosen shop in the menu; closing puts focus back on the pill.
  _setMenu(open) {
    this._menuOpen = open;
    this._render();
    const target = open ? this.shadowRoot.querySelector(".shop-option.on") : this.shadowRoot.querySelector(".shop-pick");
    target?.focus({ preventScroll: true });
  }

  // Escape closes the menu; arrow keys move between its entries.
  _onKeyDown(e) {
    if (!this._menuOpen) return;
    if (e.key === "Escape") {
      e.preventDefault();
      this._setMenu(false);
    } else if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      const options = [...this.shadowRoot.querySelectorAll(".shop-option")];
      const at = options.indexOf(this.shadowRoot.activeElement);
      options[(at + (e.key === "ArrowDown" ? 1 : options.length - 1)) % options.length]?.focus();
    }
  }

  // Desktop: the shop's own override (e.g. a microsoft-edge: link), else the web link.
  // Android with android_package: an intent link naming the app, falling back to the web page.
  _orderUrlTemplate(shop) {
    const c = this._config;
    const web = c.order_url;
    if (web && c.android_package && isAndroid() && /^https?:\/\//i.test(web)) {
      const [scheme, rest] = web.split("://");
      return `intent://${rest}#Intent;scheme=${scheme};package=${c.android_package};S.browser_fallback_url=${encodeURIComponent(web)};end`;
    }
    return shop.order_url !== undefined ? shop.order_url : web;
  }

  // shopName: shown on the order in the combined list's "All shops" view.
  _order(o, orderUrl, accent, shopName = null) {
    const c = this._config;
    const due = this._due(o.ship_by_ts);
    const buyer = (o.buyer_name || "").trim() || `#${o.receipt_id}`;
    const price = c.show_prices && o.order_total != null ? `<span class="price">${esc(this._money(o.order_total, o.currency_code))}</span>` : "";
    // The placeholder also appears URL-encoded inside an Android intent's fallback URL.
    const url = orderUrl ? String(orderUrl).replace(/\{receipt_id\}|%7Breceipt_id%7D/g, encodeURIComponent(o.receipt_id)) : null;
    // A variation pill or personalization note, tinted by the colors it names. A "random" choice
    // gets the random effect instead, seeded by order and position so it stays put between renders.
    const box = (tag, cls, text, seed, small, attrs = "") => {
      const b = this._colors.box(text, `${o.receipt_id}|${seed}`, small);
      if (!b.style) return `<${tag} class="${cls}"${attrs}>${esc(text)}</${tag}>`;
      return `<${tag} class="${cls}${b.cls}" style="${esc(b.style)}"${attrs}>${b.inner}${esc(text)}</${tag}>`;
    };
    const items = (o.items || [])
      .map((i, ii) => {
        const vars = (i.variations || []).map((v, vi) => box("span", "var", v.value, `${ii}.${vi}`, true, ` title="${esc(v.property)}"`)).join("");
        const qty = (i.quantity || 1) > 1 ? `<span class="qty">×${i.quantity}</span>` : "";
        const note = i.personalization ? box("div", "note", i.personalization, `${ii}.note`, false) : "";
        const img = i.image_url ? `<img src="${esc(i.image_url)}" alt="" loading="lazy">` : `<div class="noimg"></div>`;
        const units = this._units(i);
        const made = this._made(o, i);
        const done = made >= units;
        return `<div class="item${done && this._canMark(i) ? " made" : ""}">${img}<div class="item-body"><div class="title">${qty}${esc(shortTitle(i.title))}</div>${vars ? `<div class="vars">${vars}</div>` : ""}${note}</div>${this._madeButton(o, i, units, made)}</div>`;
      })
      .join("");
    // Only web links get a new tab; an OS handler link (another browser) would leave a blank one behind.
    const target = url && /^https?:/i.test(url) ? ` target="_blank" rel="noopener"` : "";
    // An urgent order keeps its red background; the shop's outline shows either way.
    const cls = `order${due.urgent ? " urgent" : ""}${accent ? " accent" : ""}`;
    const style = accent ? ` style="--shop: ${esc(accent)}"` : "";
    const open = url ? `<a class="${cls}" href="${esc(url)}"${target}${style}>` : `<div class="${cls}"${style}>`;
    return `${open}<div class="order-head"><span class="due ${due.cls}" style="${due.style}">${esc(due.text)}</span><span class="buyer">${esc(buyer)}</span>${shopName ? `<span class="order-shop">${esc(shopName)}</span>` : ""}${price}</div>${items}${url ? "</a>" : "</div>"}`;
  }

  // Units to make of an item, as the app counts them (at least one).
  _units(i) {
    return Math.max(1, i.quantity || 1);
  }

  // Whether the item can be marked made here: the app sends made_quantity (integration 0.4.0 or later).
  _canMark(i) {
    return this._config.show_made && i.made_quantity !== undefined && i.transaction_id !== undefined;
  }

  // Units marked made: a tap still being saved wins over the sensor until the sensor shows it.
  _made(o, i) {
    const key = `${o.receipt_id}|${i.transaction_id}`;
    const pending = this._pending?.get(key);
    if (pending !== undefined && pending === (i.made_quantity || 0)) this._pending.delete(key);
    const made = this._pending?.get(key) ?? i.made_quantity ?? 0;
    return Math.min(this._units(i), Math.max(0, made));
  }

  // The Made checkbox: ticks one unit made per tap ("1/3", "2/3", "3/3"), and a tap on a fully made item clears it.
  _madeButton(o, i, units, made) {
    if (!this._canMark(i)) return "";
    const done = made >= units;
    const next = done ? 0 : made + 1;
    const title = done ? "Made. Tap to unmark" : units > 1 ? `${made} of ${units} made. Tap to mark one more` : "Tap to mark made";
    return `<button type="button" class="made-btn${done ? " on" : made ? " part" : ""}" aria-pressed="${done ? "true" : made ? "mixed" : "false"}"
      title="${esc(title)}" aria-label="${esc(`${shortTitle(i.title)}: ${title}`)}" data-receipt="${esc(o.receipt_id)}"
      data-transaction="${esc(i.transaction_id)}" data-next="${next}"><span class="box">${made ? "✓" : ""}</span>${units > 1 ? `<span class="count">${made}/${units}</span>` : ""}</button>`;
  }

  // A Made checkbox sits inside the order's link: the tap marks the item and doesn't open the order.
  _onClick(e) {
    if (this._onShopClick(e.composedPath())) return;
    const btn = e.composedPath().find((el) => el.classList?.contains("made-btn"));
    if (!btn) return;
    e.preventDefault();
    e.stopPropagation();
    const receipt_id = btn.dataset.receipt;
    const transaction_id = btn.dataset.transaction;
    const made = Number(btn.dataset.next);
    const key = `${receipt_id}|${transaction_id}`;
    this._pending = this._pending || new Map();
    this._pending.set(key, made);
    this._render();
    this._hass.callService("etsy_shop_manager", "set_made", { receipt_id, transaction_id, made }).catch((err) => {
      this._pending.delete(key);
      this._render();
      this.dispatchEvent(new CustomEvent("hass-notification", {
        detail: { message: `Couldn't save the Made mark: ${err?.message || err}` }, bubbles: true, composed: true,
      }));
    });
  }

  _render() {
    if (!this._hass || !this._config) return;
    if (!this.shadowRoot) {
      this.attachShadow({ mode: "open" });
      this.shadowRoot.addEventListener("click", (e) => this._onClick(e));
      this.shadowRoot.addEventListener("keydown", (e) => this._onKeyDown(e));
    }
    this.shadowRoot.innerHTML = `
      <style>
        ha-card { padding: 12px; display: flex; flex-direction: column; gap: 16px; }
        .shop { display: flex; flex-direction: column; gap: 8px; }
        .shop-head { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; }
        .shop-icon { font-size: 20px; }
        .shop-name { font-size: 18px; font-weight: 500; color: var(--primary-text-color); }
        .chips { display: flex; flex-wrap: wrap; gap: 4px; margin-left: auto; }
        /* combine_shops: the shop picker is a chip (same pill as the counts beside it) holding only the shop's icon; its
           menu lists each shop's icon and full name. */
        .shop-pick-wrap { position: relative; flex: none; }
        .chip.shop-pick { display: inline-flex; align-items: center; justify-content: center; min-height: 20px; font: inherit; font-size: 14px;
                          line-height: 1; color: var(--primary-text-color); cursor: pointer; }
        .chip.shop-pick:hover, .chip.shop-pick.open { border-color: var(--divider-color, rgba(127, 127, 127, 0.4)); }
        .shop-pick:focus-visible, .shop-option:focus-visible { outline: 2px solid var(--primary-color); outline-offset: 1px; }
        .storefront { width: 16px; height: 16px; display: block; color: #f1823a; }
        .shop-menu { position: absolute; z-index: 2; top: calc(100% + 4px); left: 0; min-width: 200px; max-width: 80vw; padding: 4px;
                     display: flex; flex-direction: column; border-radius: 12px; border: 1px solid var(--divider-color, rgba(127, 127, 127, 0.3));
                     background: var(--card-background-color, var(--ha-card-background, #1c1c1c)); box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35); }
        .shop-option { display: flex; align-items: center; gap: 8px; padding: 6px 10px; border: 0; border-radius: 8px; background: none;
                       color: var(--primary-text-color); font: inherit; font-size: 14px; text-align: left; cursor: pointer; }
        .shop-option:hover { background: rgba(127, 127, 127, 0.15); }
        .shop-option.on { background: var(--secondary-background-color, rgba(127, 127, 127, 0.2)); font-weight: 500; }
        .shop-option .mark { width: 20px; display: inline-flex; justify-content: center; flex: none; }
        .shop-option .name { flex: 1; white-space: nowrap; }
        .shop-option .count { font-size: 12px; color: var(--secondary-text-color); }
        .order-shop { flex: none; font-size: 12px; color: var(--secondary-text-color); white-space: nowrap; }
        .chip { font-size: 12px; padding: 1px 8px; border-radius: 10px; color: var(--secondary-text-color); border: 1px solid transparent;
                background: var(--secondary-background-color, rgba(127, 127, 127, 0.15)); }
        .chip.next { color: var(--primary-text-color); font-weight: 500; }
        .chip.late, .due.late { font-weight: 700; }
        .make { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 6px; font-size: 12px; color: var(--secondary-text-color); }
        .make-label { text-transform: uppercase; letter-spacing: 0.05em; font-size: 11px; margin-right: 2px; }
        .make-item { padding: 2px 8px; border-radius: 10px; border: 1px solid var(--divider-color, rgba(127, 127, 127, 0.3)); color: var(--primary-text-color); }
        .order { display: flex; flex-direction: column; gap: 8px; padding: 10px; border-radius: 12px; text-decoration: none; color: inherit;
                 background: var(--secondary-background-color, rgba(127, 127, 127, 0.08)); transition: background 0.15s; }
        a.order:hover { background: rgba(127, 127, 127, 0.18); }
        .order.urgent { background: rgba(219, 68, 55, 0.16); box-shadow: inset 0 0 0 1px rgba(219, 68, 55, 0.5); }
        a.order.urgent:hover { background: rgba(219, 68, 55, 0.26); }
        /* The shop's color as a tab on the left and a thin outline, like personalization notes and the app's listing cards. */
        .order.accent, .order.urgent.accent { box-shadow: inset 4px 0 0 var(--shop), inset 0 0 0 1px var(--shop); }
        .order-head { display: flex; align-items: center; gap: 8px; min-width: 0; }
        .due { flex: none; font-size: 12px; font-weight: 500; padding: 1px 8px; border-radius: 10px; color: var(--primary-text-color);
               background: rgba(127, 127, 127, 0.18); border: 1px solid transparent; }
        .buyer { flex: 1; min-width: 0; font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .price { flex: none; font-size: 13px; color: var(--secondary-text-color); }
        .item { display: flex; gap: 10px; align-items: flex-start; }
        .item img, .noimg { flex: none; width: 56px; height: 44px; border-radius: 8px; object-fit: cover; background: rgba(127, 127, 127, 0.2); }
        .item-body { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 4px; }
        .title { font-size: 14px; line-height: 1.3; color: var(--primary-text-color); }
        .qty { display: inline-block; margin-right: 6px; padding: 0 6px; border-radius: 8px; font-weight: 600; font-size: 12px;
               color: var(--text-primary-color, #fff); background: var(--primary-color); }
        .vars { display: flex; flex-wrap: wrap; gap: 4px; }
        /* Made checkbox: a box that fills in the success color; partly made items show the count beside it. */
        .made-btn { flex: none; display: inline-flex; align-items: center; gap: 4px; align-self: center; margin: -4px -4px -4px 0;
                    padding: 6px; border: 0; background: none; color: var(--secondary-text-color); font: inherit; font-size: 12px;
                    cursor: pointer; border-radius: 8px; -webkit-tap-highlight-color: transparent; }
        .made-btn:hover { background: rgba(127, 127, 127, 0.15); }
        .made-btn:focus-visible { outline: 2px solid var(--primary-color); outline-offset: 1px; }
        .made-btn .box { width: 20px; height: 20px; display: inline-flex; align-items: center; justify-content: center; box-sizing: border-box;
                         border: 2px solid var(--secondary-text-color); border-radius: 5px; font-size: 14px; font-weight: 700; line-height: 1; }
        .made-btn.part .box { border-color: var(--success-color, #43a047); color: var(--success-color, #43a047); }
        .made-btn.on .box { border-color: var(--success-color, #43a047); background: var(--success-color, #43a047); color: #fff; }
        .made-btn.on, .made-btn.part { color: var(--primary-text-color); }
        .item.made img, .item.made .noimg, .item.made .item-body { opacity: 0.55; }
        .item.made .title { text-decoration: line-through; }
        .make-done { color: var(--success-color, #43a047); font-weight: 500; }
        .var { font-size: 11px; padding: 1px 6px; border-radius: 8px; color: var(--secondary-text-color);
               border: 1px solid var(--divider-color, rgba(127, 127, 127, 0.3)); }
        .note { font-size: 13px; padding: 4px 8px; border-left: 3px solid var(--accent-color, var(--primary-color));
                border-radius: 4px; background: rgba(127, 127, 127, 0.1); color: var(--primary-text-color); white-space: pre-wrap; }
        ${EtsyOrderColors.CSS}
        /* One switch for every animation on the card (random, metal and any added later): while the card is
           paused (window unfocused or hidden, see _updatePaused) all of them hold their frame, and they resume
           in place. With reduced motion requested, none of them run. */
        ha-card.paused *, ha-card.paused *::before, ha-card.paused *::after { animation-play-state: paused !important; }
        @media (prefers-reduced-motion: reduce) { ha-card *, ha-card *::before, ha-card *::after { animation: none !important; } }
        .empty { color: var(--secondary-text-color); font-size: 14px; padding: 4px 0; }
      </style>
      <ha-card class="${this._paused ? "paused" : ""}" style="--phase: -${this._animClock() % 240000}ms">${this._config.combine_shops ? this._combined() : this._config.shops.map((s) => this._shop(s)).join("")}</ha-card>`;
  }
}

customElements.define("etsy-orders-card", EtsyOrdersCard);
window.customCards = window.customCards || [];
window.customCards.push({
  type: "etsy-orders-card",
  name: "Etsy Orders",
  description: "Open Etsy orders as order cards with thumbnails, variations, personalization and ship-by urgency.",
});
