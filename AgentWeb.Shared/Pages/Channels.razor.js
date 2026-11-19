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
        if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
            e.preventDefault();
            if (textarea.value.trim().length > 0) textarea.form?.requestSubmit();
            setTimeout(grow, 0);
        }
    });
    grow();
}
