// Tema scelto in Impostazioni → Aspetto: "system" (default), "light" o "dark", salvato solo su questo dispositivo.
// Caricato in <head> senza defer, così la pagina non lampeggia col tema sbagliato.
(() => {
    const key = "agentweb-theme";
    const read = () => { try { return localStorage.getItem(key) || "system"; } catch { return "system"; } };
    const apply = () => {
        const t = read();
        if (t === "system") delete document.documentElement.dataset.theme;
        else document.documentElement.dataset.theme = t;
    };
    window.agentwebTheme = {
        get: read,
        set: t => { try { t === "system" ? localStorage.removeItem(key) : localStorage.setItem(key, t); } catch { } apply(); },
    };
    apply();
    // La navigazione "enhanced" di Blazor può riscrivere l'<html>: si riapplica dopo ogni pagina.
    window.addEventListener("load", () => window.Blazor?.addEventListener?.("enhancedload", apply));
})();
