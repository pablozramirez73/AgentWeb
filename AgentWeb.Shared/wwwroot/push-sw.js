// Service worker delle notifiche push (solo browser). Lo scope è _content/AgentWeb.Shared/: non intercetta le pagine, riceve solo i push.
const appBase = self.registration.scope.replace(/_content\/AgentWeb\.Shared\/$/, "");

self.addEventListener("push", event => {
    let n = { title: "AgentWeb", body: "", url: "" };
    try { n = { ...n, ...event.data.json() }; } catch { }
    event.waitUntil((async () => {
        // Chi ha già AgentWeb aperto e in primo piano vede il messaggio in tempo reale: niente notifica doppia.
        const open = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
        if (open.some(c => c.focused && c.url.startsWith(appBase))) return;
        await self.registration.showNotification(n.title, { body: n.body, tag: n.url || undefined, data: { url: n.url } });
    })());
});

self.addEventListener("notificationclick", event => {
    event.notification.close();
    const target = new URL(event.notification.data?.url || "", appBase).href;
    event.waitUntil((async () => {
        const open = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
        const tab = open.find(c => c.url.startsWith(appBase));
        if (tab) { await tab.focus(); return tab.navigate(target); }
        return self.clients.openWindow(target);
    })());
});
