// The print station (Components/Pages/Orders/PrintStation.razor): asks the app for queued packing slips every few
// seconds, prints them in a hidden frame (the same printout as Orders -> Print packing slips) and reports them printed.
// With the browser started with --kiosk-printing, print() goes straight to the default printer without a dialog.
(function () {
    const interval = 10000;
    const status = document.getElementById("ps-status");
    const log = document.getElementById("ps-log");
    let busy = false;
    let unreported = null; // ids printed but not yet reported (the app was unreachable): reported before printing more

    function setStatus(text, problem) {
        status.textContent = text;
        status.classList.toggle("is-problem", !!problem);
    }

    function time() {
        return new Date().toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    }

    function addLog(text) {
        if (log.querySelector(".muted")) log.textContent = "";
        const item = document.createElement("li");
        item.textContent = text;
        log.prepend(item);
        while (log.children.length > 20) log.lastElementChild.remove();
    }

    async function post(url, body) {
        const res = await fetch(url, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body ?? {}),
            cache: "no-store",
        });
        if (!res.ok) throw new Error("The app answered " + res.status);
        return res.json();
    }

    // Loads a same-origin page in a hidden frame, waits for its images and fonts, prints it, then removes the frame.
    // print() returns once the job is handed to the printer (or the dialog closes).
    function printFrame(setup) {
        return new Promise(resolve => {
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
                // Leave the frame a moment: some printers read it after print() returns.
                setTimeout(() => { frame.remove(); resolve(); }, 2000);
            };
            setup(frame);
            document.body.appendChild(frame);
        });
    }

    async function tick() {
        if (busy) return;
        busy = true;
        try {
            if (unreported) {
                await post("orders/print-station/api/printed", { ids: unreported });
                unreported = null;
            }
            const next = await post("orders/print-station/api/claim");
            if (next.ids && next.ids.length > 0) {
                const count = next.ids.length;
                setStatus(`Printing ${count} packing slip${count === 1 ? "" : "s"}…`);
                await printFrame(f => { f.src = next.url; });
                unreported = next.ids;
                await post("orders/print-station/api/printed", { ids: unreported });
                unreported = null;
                addLog(`${time()}: ${count} packing slip${count === 1 ? "" : "s"}`);
            }
            setStatus(`Waiting for orders. Last checked at ${time()}.`);
        } catch (e) {
            setStatus(`Can't reach the app (${e.message}). Trying again shortly.`, true);
        } finally {
            busy = false;
        }
    }

    document.getElementById("ps-test").addEventListener("click", async () => {
        await printFrame(f => {
            f.srcdoc = "<!doctype html><title>Print station test</title><body style=\"font: 14pt sans-serif; margin: 1in\">"
                + "<h1>Print station test page</h1><p>Packing slips from Etsy Shop Manager will print on this printer.</p>"
                + `<p>${new Date().toLocaleString()}</p></body>`;
        });
        addLog(`${time()}: test page`);
    });

    tick();
    setInterval(tick, interval);
})();
