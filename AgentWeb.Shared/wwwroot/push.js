// Iscrizione del browser alle notifiche push (Web Push, chiavi VAPID). Chiamato dalla pagina Impostazioni → Notifiche.
const sw = "_content/AgentWeb.Shared/push-sw.js";

async function registration() {
    if (!("serviceWorker" in navigator) || !("PushManager" in window)) return null;
    return navigator.serviceWorker.register(new URL(sw, document.baseURI).href);
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
    if (!reg || await Notification.requestPermission() !== "granted") return null;
    await navigator.serviceWorker.ready;
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
