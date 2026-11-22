// Iscrizione del browser alle notifiche push (Web Push, chiavi VAPID). Chiamato dalla pagina Impostazioni → Notifiche.
const sw = "_content/AgentWeb.Shared/push-sw.js";

// iOS chiede il permesso solo dentro il tocco: il clic di Blazor arriva qui dopo un giro al server (InteractiveServer),
// troppo tardi. Quindi lo chiediamo subito al tocco su "Attiva" e subscribe() riusa la stessa risposta.
let asked = null;
document.addEventListener("click", e => {
    if (e.target.closest?.("[data-push-enable]") && "Notification" in window && Notification.permission === "default")
        asked = Notification.requestPermission();
}, true);

async function registration() {
    if (!("serviceWorker" in navigator) || !("PushManager" in window)) return null;
    return navigator.serviceWorker.register(new URL(sw, document.baseURI).href);
}

// Non navigator.serviceWorker.ready: si risolve solo per un worker che controlla questa pagina, e il nostro ha scope
// /_content/AgentWeb.Shared/, quindi resterebbe in attesa per sempre. Per iscriversi basta che questa registrazione sia attiva.
async function activated(reg) {
    const w = reg.installing || reg.waiting || reg.active;
    if (!w || w.state === "activated") return;
    const done = new Promise(resolve => w.addEventListener("statechange", () => { if (w.state === "activated") resolve(); }));
    const late = new Promise((_, reject) => setTimeout(() => reject(new Error("Il browser non ha attivato il servizio per le notifiche: ricarica la pagina e riprova.")), 15000));
    await Promise.race([done, late]);
}

function keyBytes(base64url) {
    const s = atob(base64url.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(base64url.length / 4) * 4, "="));
    return Uint8Array.from(s, c => c.charCodeAt(0));
}

function plain(sub) {
    const j = sub.toJSON();
    return { kind: "web", token: j.endpoint, p256dh: j.keys.p256dh, auth: j.keys.auth };
}

export function supported() { return "serviceWorker" in navigator && "PushManager" in window && "Notification" in window; }

export async function current() {
    const reg = await registration();
    const sub = reg && await reg.pushManager.getSubscription();
    return sub ? plain(sub) : null;
}

// null = permesso negato o browser senza Web Push (su iPhone: solo dopo "Aggiungi alla schermata Home").
export async function subscribe(vapidPublicKey) {
    const reg = await registration();
    if (!reg || await (asked ?? Notification.requestPermission()) !== "granted") return null;
    await activated(reg);
    let sub = await reg.pushManager.getSubscription();
    // Chiave del server cambiata: la vecchia iscrizione non funzionerebbe più.
    const key = keyBytes(vapidPublicKey);
    if (sub && sub.options.applicationServerKey && new Uint8Array(sub.options.applicationServerKey).toString() !== key.toString()) {
        await sub.unsubscribe(); sub = null;
    }
    sub ??= await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
    return plain(sub);
}

export async function unsubscribe() {
    const reg = await registration();
    const sub = reg && await reg.pushManager.getSubscription();
    if (sub) await sub.unsubscribe();
}
