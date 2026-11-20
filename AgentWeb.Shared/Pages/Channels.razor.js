// Invio con Invio, a capo con Maiusc+Invio; la casella cresce con il testo (fino a 8 righe).
export function enterToSend(textarea) {
    if (!textarea || textarea.dataset.enterToSend) return;
    textarea.dataset.enterToSend = "1";

    const grow = () => {
        textarea.style.height = "auto";
        textarea.style.height = Math.min(textarea.scrollHeight, 8 * 22) + "px";
    };

    textarea.addEventListener("input", grow);
    textarea.addEventListener("keydown", e => {
        // Menu delle menzioni aperto: Invio o Tab sceglie il primo agente invece di inviare.
        const menu = textarea.form?.previousElementSibling;
        if ((e.key === "Enter" || e.key === "Tab") && !e.shiftKey && !e.isComposing && menu?.classList.contains("mention-menu")) {
            const first = menu.querySelector("button");
            if (first) { e.preventDefault(); first.click(); return; }
        }
        if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
            e.preventDefault();
            if (textarea.value.trim().length > 0) textarea.form?.requestSubmit();
            setTimeout(grow, 0);
        }
    });
    grow();
}
