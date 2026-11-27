using Microsoft.EntityFrameworkCore;

using static AgentWeb.Shared.Lang;

namespace AgentWeb.Web;

// L'UNICO posto che decide chi vede un canale. Lo usano lista canali, messaggi, task, membri (HTTP),
// Watch dell'hub SignalR, replay e il filtro degli eventi lato server. Nessun altro codice deve riscrivere la regola.
// Regola: pubblico -> ogni utente autenticato; privato -> solo i membri (gli admin non hanno accesso laterale).
public static class ChannelVisibility
{
    public static IQueryable<ChannelEntity> Visible(AppDb db, string userName) =>
        db.Channels.Where(c => !c.IsPrivate || db.ChannelMembers.Any(m => m.ChannelId == c.Id && m.UserName == userName));

    public static Task<bool> CanAccessAsync(AppDb db, int channelId, string userName) =>
        Visible(db, userName).AnyAsync(c => c.Id == channelId);

    // Per chi non vede il canale è come se non esistesse: 404, non 403 (non rivela che c'è).
    public static async Task EnsureAccessAsync(AppDb db, int channelId, string userName)
    {
        if (!await CanAccessAsync(db, channelId, userName)) throw new KeyNotFoundException(T("nf.channel", channelId));
    }
}
