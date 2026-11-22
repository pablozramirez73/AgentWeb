using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AgentWeb.Shared.Workspace;
using Microsoft.EntityFrameworkCore;

namespace AgentWeb.Web;

// Eventi in tempo reale dentro il processo: li ascoltano le pagine Blazor Server e l'hub SignalR (che li instrada per canale).
public sealed class WorkspaceBus
{
    public event Action<WorkspaceEvent>? Published;

    public void Publish(WorkspaceEvent e)
    {
        foreach (var h in Published?.GetInvocationList().Cast<Action<WorkspaceEvent>>() ?? [])
            try { h(e); } catch { /* un client rotto non deve fermare gli altri */ }
    }
}

// ParentId: risponde in quel thread. MessageId: il messaggio che l'ha attivato (la richiesta, anche senza @menzione nei messaggi diretti).
public sealed record AgentJob(int ChannelId, string Agent, int? TaskId, int Depth, long? ParentId = null, long? MessageId = null);

public sealed class AgentQueue
{
    readonly Channel<AgentJob> jobs = Channel.CreateUnbounded<AgentJob>();
    public void Enqueue(AgentJob job) => jobs.Writer.TryWrite(job);
    public IAsyncEnumerable<AgentJob> ReadAllAsync(CancellationToken ct) => jobs.Reader.ReadAllAsync(ct);
}

// Metodi con parametro "user": chiamati per conto di una persona, passano da ChannelVisibility.
// PostAsync/StartTaskAsync/FinishTaskAsync: autorità interna del server (agenti e messaggi di sistema).
public sealed partial class WorkspaceStore(IDbContextFactory<AppDb> dbf, WorkspaceBus bus, AgentQueue queue, AgentSettingsStore settings, MemoryStore memory, IConfiguration config, AgentIcons icons)
{
    const int MaxMentionDepth = 3;          // agente che menziona agente: massimo 3 salti, niente loop infiniti
    public const int NoMentions = MaxMentionDepth; // depth per messaggi le cui @menzioni non devono attivare agenti (deleghe)
    readonly int replayHorizon = config.GetValue("Workspace:ReplayHorizon", 500);
    // Inserimento + pubblicazione serializzati per canale: gli eventi escono in ordine di Id (ordine monotono per canale).
    // shortcut: lock in memoria, vale per una replica; con più repliche l'ordine lo ricostruisce il client ordinando per Id.
    readonly ConcurrentDictionary<int, SemaphoreSlim> channelLocks = new();

    public async Task<List<ChannelDto>> GetChannelsAsync(string user)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var mention = MentionPattern(user);
        // shortcut: conteggi calcolati da Postgres a ogni lettura della lista (subquery per canale); con molti canali/messaggi
        // servirebbe una proiezione mantenuta (es. tabella aggiornata dall'insert o un motore di streaming).
        return (await ChannelVisibility.Visible(db, user).OrderBy(c => c.Name).Select(c => new
        {
            c,
            Last = db.ReadCursors.Where(r => r.ChannelId == c.Id && r.UserName == user).Select(r => (long?)r.LastReadId).FirstOrDefault() ?? 0,
        }).Select(x => new
        {
            Dto = new ChannelDto(x.c.Id, x.c.Name, x.c.IsPrivate, x.c.CreatedBy,
                db.Messages.Count(m => m.ChannelId == x.c.Id && m.Id > x.Last && m.Author != user && m.Kind != AuthorKind.System),
                db.Messages.Count(m => m.ChannelId == x.c.Id && m.Id > x.Last && m.Author != user && m.Kind != AuthorKind.System && EF.Functions.ILike(m.Text, mention, "\\")), null),
            x.c.DirectKey,
        }).ToListAsync()).Select(x => x.Dto with { With = DirectWith(x.DirectKey, user) }).ToList();
    }

    // "@alice" per alice@esempio.it; % e _ dell'email non devono fare da jolly.
    static string MentionPattern(string user) => "%@" + user.Split('@')[0].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    // Il cursore avanza soltanto (GREATEST): un client in ritardo non fa ricomparire messaggi già letti.
    public async Task MarkReadAsync(int channelId, string user, long lastId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ReadCursors" ("UserName", "ChannelId", "LastReadId") VALUES ({user}, {channelId}, {lastId})
            ON CONFLICT ("UserName", "ChannelId") DO UPDATE SET "LastReadId" = GREATEST("ReadCursors"."LastReadId", EXCLUDED."LastReadId")
            """);
    }

    public async Task MarkThreadReadAsync(int channelId, string user, long rootId, long lastId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        if (!await db.Messages.AnyAsync(m => m.Id == rootId && m.ChannelId == channelId && m.ParentId == null)) throw new KeyNotFoundException($"Thread {rootId} inesistente.");
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ThreadReadCursors" ("UserName", "RootId", "LastReadId") VALUES ({user}, {rootId}, {lastId})
            ON CONFLICT ("UserName", "RootId") DO UPDATE SET "LastReadId" = GREATEST("ThreadReadCursors"."LastReadId", EXCLUDED."LastReadId")
            """);
    }

    // Inbox della pagina Attività: i canali visibili con l'ultimo messaggio fuori dai thread, più i thread che riguardano
    // l'utente (radice o risposta sua, oppure una sua @menzione). Tutto passa da ChannelVisibility.
    // shortcut: calcolato a ogni lettura (subquery per canale/thread, al massimo 40 thread); con molti dati servirebbe una proiezione mantenuta.
    public async Task<List<FeedItemDto>> GetFeedAsync(string user)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var mention = MentionPattern(user);
        var visible = ChannelVisibility.Visible(db, user);
        var channels = await visible.Select(c => new
        {
            c.Id, c.Name, c.IsPrivate, c.DirectKey,
            Last = db.Messages.Where(m => m.ChannelId == c.Id && m.ParentId == null).OrderByDescending(m => m.Id).FirstOrDefault(),
            Unread = db.Messages.Count(m => m.ChannelId == c.Id && m.ParentId == null && m.Author != user && m.Kind != AuthorKind.System
                && m.Id > (db.ReadCursors.Where(r => r.ChannelId == c.Id && r.UserName == user).Select(r => (long?)r.LastReadId).FirstOrDefault() ?? 0)),
        }).Where(x => x.Last != null).ToListAsync();

        var threads = await db.Messages
            .Where(r => r.ParentId == null && visible.Any(c => c.Id == r.ChannelId) && db.Messages.Any(x => x.ParentId == r.Id)
                && (r.Author == user || EF.Functions.ILike(r.Text, mention, "\\")
                    || db.Messages.Any(x => x.ParentId == r.Id && (x.Author == user || EF.Functions.ILike(x.Text, mention, "\\")))))
            .Select(r => new
            {
                Root = r,
                Last = db.Messages.Where(x => x.ParentId == r.Id).OrderByDescending(x => x.Id).First(),
                Replies = db.Messages.Count(x => x.ParentId == r.Id),
                Read = Math.Max(
                    db.ReadCursors.Where(c => c.ChannelId == r.ChannelId && c.UserName == user).Select(c => (long?)c.LastReadId).FirstOrDefault() ?? 0,
                    db.ThreadReadCursors.Where(c => c.RootId == r.Id && c.UserName == user).Select(c => (long?)c.LastReadId).FirstOrDefault() ?? 0),
            })
            .OrderByDescending(x => x.Last.Id).Take(40)
            .Select(x => new { x.Root, x.Last, x.Replies, Unread = db.Messages.Count(m => m.ParentId == x.Root.Id && m.Author != user && m.Id > x.Read) })
            .ToListAsync();

        var byId = channels.ToDictionary(c => c.Id);
        return channels.Select(c => new FeedItemDto(c.Id, c.Name, c.IsPrivate, null, ToDto(c.Last!), 0, c.Unread, DirectWith(c.DirectKey, user)))
            .Concat(threads.Select(t => new FeedItemDto(t.Root.ChannelId, byId.TryGetValue(t.Root.ChannelId, out var c) ? c.Name : "", byId.TryGetValue(t.Root.ChannelId, out var p) && p.IsPrivate,
                ToDto(t.Root), ToDto(t.Last), t.Replies, t.Unread, byId.TryGetValue(t.Root.ChannelId, out var d) ? DirectWith(d.DirectKey, user) : null)))
            .OrderByDescending(f => f.Last.Id).ToList();
    }

    // Ricerca full-text su tutti i canali visibili (stessa regola di ChannelVisibility), i più recenti per primi.
    // Attività visibile all'utente: lavori degli agenti e task aperti dei soli canali che vede (ChannelVisibility).
    public async Task<ActivityDto> GetActivityAsync(string user, AgentActivity activity)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var channels = await ChannelVisibility.Visible(db, user).ToDictionaryAsync(c => c.Id, c => c.Name);
        var ids = channels.Keys.ToList();
        var open = await db.Tasks.Where(t => ids.Contains(t.ChannelId) && (t.Status == WorkStatus.Todo || t.Status == WorkStatus.InProgress))
            .OrderByDescending(t => t.UpdatedAt).Take(50).ToListAsync();
        var (running, recent) = activity.Snapshot();
        List<AgentRunDto> Visible(IEnumerable<AgentActivity.Run> runs) => runs.Where(r => channels.ContainsKey(r.ChannelId))
            .Select(r => new AgentRunDto(r.Agent, r.ChannelId, channels[r.ChannelId], r.Kind, r.StartedAt, r.Seconds, r.Error)).ToList();
        return new(Visible(running), Visible(recent).Take(30).ToList(), open.Select(ToDto).ToList());
    }

    public async Task<List<SearchHitDto>> SearchAsync(string user, string query, int take = 50)
    {
        query = Required(query, 200, "Ricerca");
        await using var db = await dbf.CreateDbContextAsync();
        var visible = ChannelVisibility.Visible(db, user);
        var hits = await db.Messages
            .Where(m => m.Kind != AuthorKind.System && visible.Any(c => c.Id == m.ChannelId)
                && EF.Functions.ToTsVector("simple", m.Text).Matches(EF.Functions.WebSearchToTsQuery("simple", query)))
            .OrderByDescending(m => m.Id).Take(take)
            .Join(db.Channels, m => m.ChannelId, c => c.Id, (m, c) => new { m, c.Name })
            .ToListAsync();
        return hits.Select(h => new SearchHitDto(ToDto(h.m), h.Name)).ToList();
    }

    // Thread: la radice e le sue risposte, in ordine.
    public async Task<List<MessageDto>> GetThreadAsync(int channelId, string user, long rootId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        var list = await db.Messages.Where(m => m.ChannelId == channelId && (m.Id == rootId || m.ParentId == rootId)).OrderBy(m => m.Id).ToListAsync();
        if (list.All(m => m.Id != rootId)) throw new KeyNotFoundException($"Messaggio {rootId} inesistente.");
        return list.Select(ToDto).ToList();
    }

    public async Task<bool> CanAccessAsync(int channelId, string user)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await ChannelVisibility.CanAccessAsync(db, channelId, user);
    }

    // L'altro partecipante visto da "user" (persona o agente), null se non è un messaggio diretto.
    public static string? DirectWith(string? key, string user)
    {
        if (key is null) return null;
        var other = key.Split('|').FirstOrDefault(p => p != user) ?? user; // con sé stessi: sé stessi
        return other.StartsWith("agent:") ? other[6..] : other;
    }

    // Messaggi diretti: un canale privato con i due partecipanti come membri (stesse regole di visibilità, notifiche e
    // non letti degli altri canali). Con un agente: l'utente è l'unico membro e l'agente è nel canale e risponde a ogni messaggio.
    public async Task<ChannelDto> OpenDirectAsync(string user, string with)
    {
        with = Required(with, 256, "Destinatario");
        var agent = settings.Get().EnabledAgentNames().FirstOrDefault(a => a.Equals(with.TrimStart('@'), StringComparison.OrdinalIgnoreCase));
        await using var db = await dbf.CreateDbContextAsync();
        string key;
        if (agent is not null) key = $"{user}|agent:{agent}";
        else
        {
            var person = await db.Users.Where(u => u.UserName == with).Select(u => u.UserName).FirstOrDefaultAsync()
                ?? throw new ArgumentException($"Utente \"{with}\" inesistente.");
            key = string.Join('|', new[] { user, person! }.Distinct().Order(StringComparer.Ordinal));
        }
        var existing = await db.Channels.FirstOrDefaultAsync(c => c.DirectKey == key);
        if (existing is null)
        {
            existing = new ChannelEntity { Name = $"dm-{Guid.NewGuid():N}"[..15], IsPrivate = true, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = user, DirectKey = key };
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Channels.Add(existing);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) // l'altro l'ha aperta nello stesso istante
            {
                await tx.RollbackAsync();
                await using var again = await dbf.CreateDbContextAsync();
                existing = await again.Channels.SingleAsync(c => c.DirectKey == key);
                return new(existing.Id, existing.Name, true, existing.CreatedBy, With: DirectWith(key, user));
            }
            foreach (var member in key.Split('|').Where(p => !p.StartsWith("agent:")))
                db.ChannelMembers.Add(new() { ChannelId = existing.Id, UserName = member, AddedAt = DateTimeOffset.UtcNow });
            if (agent is not null) db.ChannelAgents.Add(new() { ChannelId = existing.Id, AgentName = agent, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return new(existing.Id, existing.Name, true, existing.CreatedBy, With: DirectWith(key, user));
    }

    // Per scegliere a chi scrivere: solo persone che l'utente vede già nei suoi canali (niente elenco completo degli utenti).
    public async Task<List<string>> GetPeopleAsync(string user)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var visible = ChannelVisibility.Visible(db, user);
        var authors = db.Messages.Where(m => m.Kind == AuthorKind.User && visible.Any(c => c.Id == m.ChannelId)).Select(m => m.Author);
        var members = db.ChannelMembers.Where(m => visible.Any(c => c.Id == m.ChannelId)).Select(m => m.UserName);
        return await authors.Union(members).Where(n => n != user).OrderBy(n => n).ToListAsync();
    }

    public async Task<ChannelDto> CreateChannelAsync(string name, string user, bool isPrivate = false)
    {
        name = Regex.Replace(Required(name, 50, "Nome canale").ToLowerInvariant(), @"\s+", "-");
        await using var db = await dbf.CreateDbContextAsync();
        if (await db.Channels.FirstOrDefaultAsync(c => c.Name == name) is { } existing)
        {
            if (await ChannelVisibility.CanAccessAsync(db, existing.Id, user)) return new(existing.Id, existing.Name, existing.IsPrivate, existing.CreatedBy);
            throw new ArgumentException("Nome canale già in uso.");
        }
        var c = new ChannelEntity { Name = name, IsPrivate = isPrivate, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = user };
        await using (var tx = await db.Database.BeginTransactionAsync()) // canale privato e creatore-membro insieme, o niente
        {
            db.Channels.Add(c);
            await db.SaveChangesAsync();
            if (isPrivate) db.ChannelMembers.Add(new() { ChannelId = c.Id, UserName = user, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        var dto = new ChannelDto(c.Id, c.Name, c.IsPrivate, c.CreatedBy);
        bus.Publish(new("channel", c.Id, Channel: dto));
        await PostAsync(c.Id, "Sistema", AuthorKind.System, $"{user} ha creato il canale{(isPrivate ? " privato" : "")}.");
        return dto;
    }

    // Membri: per un canale pubblico la lista è vuota (lo vedono tutti); per un privato solo i membri possono aggiungerne.
    public async Task<List<string>> GetMembersAsync(int channelId, string user)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        return await db.ChannelMembers.Where(m => m.ChannelId == channelId).OrderBy(m => m.UserName).Select(m => m.UserName).ToListAsync();
    }

    public async Task AddMemberAsync(int channelId, string user, string newMember)
    {
        newMember = Required(newMember, 256, "Utente");
        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        var channel = await db.Channels.SingleAsync(c => c.Id == channelId);
        if (!channel.IsPrivate) throw new ArgumentException("Il canale è pubblico: lo vedono già tutti.");
        if (channel.DirectKey is not null) throw new ArgumentException("Un messaggio diretto resta tra due: crea un canale privato per più persone.");
        if (!await db.Users.AnyAsync(u => u.UserName == newMember)) throw new ArgumentException($"Utente \"{newMember}\" inesistente.");
        if (await db.ChannelMembers.AnyAsync(m => m.ChannelId == channelId && m.UserName == newMember)) return;
        db.ChannelMembers.Add(new() { ChannelId = channelId, UserName = newMember, AddedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        bus.Publish(new("channel", channelId, Channel: new(channel.Id, channel.Name, true, channel.CreatedBy))); // il nuovo membro lo vede comparire
        await PostAsync(channelId, "Sistema", AuthorKind.System, $"{user} ha aggiunto {newMember} al canale.");
    }

    public async Task<List<MessageDto>> GetMessagesAsync(int channelId, string user, int take = 200)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        return await LatestAsync(db, channelId, take);
    }

    // Replay dopo una disconnessione: fino a replayHorizon messaggi dopo afterId; oltre, Reset e il client ricarica.
    public async Task<ReplayDto> GetMessagesSinceAsync(int channelId, string user, long afterId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        var list = await db.Messages.Where(m => m.ChannelId == channelId && m.Id > afterId).OrderBy(m => m.Id).Take(replayHorizon + 1).ToListAsync();
        return list.Count > replayHorizon ? new([], Reset: true) : new(list.Select(ToDto).ToList(), Reset: false);
    }

    public async Task<MessageDto> PostUserMessageAsync(int channelId, string user, string text, string? clientId = null, long? parentId = null)
    {
        text = Required(text, 4000, "Messaggio");
        if (clientId is { Length: > 64 }) throw new ArgumentException("ClientId: massimo 64 caratteri.");
        await using (var db = await dbf.CreateDbContextAsync())
        {
            await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
            if (parentId is { } p)
                // Un solo livello: rispondere a una risposta vuol dire rispondere nel suo thread.
                parentId = await db.Messages.Where(m => m.Id == p && m.ChannelId == channelId).Select(m => (long?)(m.ParentId ?? m.Id)).FirstOrDefaultAsync()
                    ?? throw new ArgumentException($"Messaggio {p} inesistente in questo canale.");
        }
        return await PostAsync(channelId, user, AuthorKind.User, text, 0, clientId, parentId);
    }

    public async Task<MessageDto> PostAsync(int channelId, string author, AuthorKind kind, string text, int depth = 0, string? clientId = null, long? parentId = null)
    {
        MessageDto dto;
        var gate = channelLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await using var db = await dbf.CreateDbContextAsync();
            if (!await db.Channels.AnyAsync(c => c.Id == channelId)) throw new KeyNotFoundException($"Canale {channelId} inesistente.");
            // Idempotenza: stesso (canale, autore, ClientId) -> stesso messaggio, nessun nuovo evento, nessuna nuova menzione.
            if (clientId != null && await db.Messages.FirstOrDefaultAsync(m => m.ChannelId == channelId && m.Author == author && m.ClientId == clientId) is { } dup)
                return ToDto(dup);
            var m = new MessageEntity { ChannelId = channelId, Author = author, Kind = kind, Text = text.Trim(), At = DateTimeOffset.UtcNow, ClientId = clientId, ParentId = parentId };
            db.Messages.Add(m);
            await db.SaveChangesAsync();
            dto = ToDto(m);
            bus.Publish(new("message", channelId, Message: dto));
        }
        finally { gate.Release(); }

        if (kind != AuthorKind.System && depth < MaxMentionDepth)
        {
            // Rispondono solo gli agenti del canale; a una persona che menziona un agente assente si spiega perché tace.
            var inChannel = await ChannelAgentNamesAsync(channelId);
            // Risposta di una persona nel thread di un task: l'agente riprende da dove era, con la risposta come contesto.
            // shortcut: riprende a ogni risposta (anche un "grazie"); l'agente decide se c'è altro da fare.
            string? resumed = null;
            if (kind == AuthorKind.User && parentId is { } thread)
            {
                await using var db = await dbf.CreateDbContextAsync();
                if (await db.Tasks.FirstOrDefaultAsync(t => t.MessageId == thread && t.Agent != null
                        && t.Status != WorkStatus.InProgress && t.Status != WorkStatus.Closed && t.Status != WorkStatus.Backlog) is { } task
                    && inChannel.Contains(task.Agent!, StringComparer.OrdinalIgnoreCase))
                {
                    // Condizionato allo stato letto: se il worker l'ha appena preso, non lo rimettiamo "Da fare" (girerebbe due volte).
                    var observed = task.Status;
                    var now = DateTimeOffset.UtcNow;
                    if (await db.Tasks.Where(x => x.Id == task.Id && x.Status == observed).ExecuteUpdateAsync(u => u
                            .SetProperty(x => x.Assignee, task.Agent).SetProperty(x => x.AssigneeIsAgent, true)
                            .SetProperty(x => x.Status, WorkStatus.Todo).SetProperty(x => x.UpdatedAt, now)) > 0)
                    {
                        task.Assignee = task.Agent; task.AssigneeIsAgent = true; task.Status = WorkStatus.Todo; task.UpdatedAt = now;
                        bus.Publish(new("task", channelId, Task: ToDto(task)));
                        queue.Enqueue(new(channelId, task.Agent!, task.Id, 0, thread));
                    }
                    resumed = task.Agent;
                }
            }
            if (kind == AuthorKind.User && await DirectAgentAsync(channelId) is { } direct && inChannel.Contains(direct, StringComparer.OrdinalIgnoreCase)
                && !Mentions(text).Contains(direct, StringComparer.OrdinalIgnoreCase))
                queue.Enqueue(new(channelId, direct, null, depth + 1, parentId, dto.Id)); // in un messaggio diretto con un agente non serve la @menzione
            foreach (var agent in Mentions(text).Where(a => !a.Equals(author, StringComparison.OrdinalIgnoreCase) && !a.Equals(resumed, StringComparison.OrdinalIgnoreCase)))
            {
                if (inChannel.Contains(agent, StringComparer.OrdinalIgnoreCase)) queue.Enqueue(new(channelId, agent, null, depth + 1, parentId, dto.Id));
                else if (kind == AuthorKind.User)
                    await PostAsync(channelId, "Sistema", AuthorKind.System, $"@{agent} non è in questo canale: chi l'ha creato può aggiungerlo.", parentId: parentId);
            }
        }
        return dto;
    }

    async Task<string?> DirectAgentAsync(int channelId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var key = await db.Channels.Where(c => c.Id == channelId).Select(c => c.DirectKey).FirstOrDefaultAsync();
        return key?.Split("|agent:") is [_, var agent] ? agent : null;
    }

    public Task<List<string>> GetAgentsAsync() => Task.FromResult(settings.Get().EnabledAgentNames().ToList());
    public Task<Dictionary<string, string>> GetAgentIconsAsync() => Task.FromResult(icons.For(settings.Get().LocalAgents.Where(a => a.Enabled)));

    // Memoria del canale vista dalle persone: chi vede il canale la consulta e carica documenti;
    // cancella chi ha creato l'elemento o il canale.
    public async Task<List<MemoryDto>> GetChannelMemoryAsync(int channelId, string user)
    {
        await using (var db = await dbf.CreateDbContextAsync()) await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        return await memory.ListChannelAsync(channelId);
    }

    public async Task<int> UploadDocumentAsync(int channelId, string user, string fileName, string text)
    {
        await using (var db = await dbf.CreateDbContextAsync()) await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        var chunks = await memory.AddDocumentAsync(channelId, fileName, text, user);
        await PostAsync(channelId, "Sistema", AuthorKind.System, $"{user} ha caricato il documento «{fileName}» nella memoria del canale.");
        return chunks;
    }

    public async Task DeleteChannelMemoryAsync(int channelId, string user, long memoryId, string? sourceId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        var owner = await db.Channels.Where(c => c.Id == channelId).Select(c => c.CreatedBy).SingleAsync();
        var author = await db.Memories.Where(m => m.ChannelId == channelId && (sourceId != null ? m.SourceId == sourceId : m.Id == memoryId))
            .Select(m => m.CreatedBy).FirstOrDefaultAsync() ?? throw new KeyNotFoundException("Elemento della memoria inesistente.");
        if (author != user && owner != user) throw new UnauthorizedAccessException("Può cancellarlo solo chi l'ha creato o chi ha creato il canale.");
        if (sourceId != null) await memory.DeleteDocumentAsync(channelId, sourceId); else await memory.DeleteChannelMemoryAsync(channelId, memoryId);
    }

    // Agenti del canale ancora abilitati nelle impostazioni (autorità interna: menzioni, task, deleghe).
    public async Task<List<string>> ChannelAgentNamesAsync(int channelId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var chosen = await db.ChannelAgents.Where(a => a.ChannelId == channelId).OrderBy(a => a.AddedAt).Select(a => a.AgentName).ToListAsync();
        var enabled = settings.Get().EnabledAgentNames().ToList();
        return chosen.Select(n => enabled.FirstOrDefault(e => e.Equals(n, StringComparison.OrdinalIgnoreCase))).OfType<string>().ToList();
    }

    public async Task<ChannelAgentsDto> GetChannelAgentsAsync(int channelId, string user)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        var owner = await db.Channels.Where(c => c.Id == channelId).Select(c => c.CreatedBy).SingleAsync();
        var agents = await ChannelAgentNamesAsync(channelId);
        var available = settings.Get().EnabledAgentNames().Where(n => !agents.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        return new(agents, available, owner == user);
    }

    // Solo chi ha creato il canale sceglie i suoi agenti.
    public async Task AddChannelAgentAsync(int channelId, string user, string agent)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await EnsureOwnerAsync(db, channelId, user);
        var name = settings.Get().EnabledAgentNames().FirstOrDefault(a => a.Equals(agent?.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Agente \"{agent}\" inesistente o disabilitato.");
        if (await db.ChannelAgents.AnyAsync(a => a.ChannelId == channelId && a.AgentName == name)) return;
        db.ChannelAgents.Add(new() { ChannelId = channelId, AgentName = name, AddedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        bus.Publish(new("agents", channelId));
        await PostAsync(channelId, "Sistema", AuthorKind.System, $"{user} ha aggiunto l'agente {name} al canale.");
    }

    public async Task RemoveChannelAgentAsync(int channelId, string user, string agent)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await EnsureOwnerAsync(db, channelId, user);
        var removed = await db.ChannelAgents.Where(a => a.ChannelId == channelId && a.AgentName.ToLower() == agent.Trim().ToLower()).ExecuteDeleteAsync();
        if (removed == 0) return;
        bus.Publish(new("agents", channelId));
        await PostAsync(channelId, "Sistema", AuthorKind.System, $"{user} ha tolto l'agente {agent.Trim()} dal canale.");
    }

    static async Task EnsureOwnerAsync(AppDb db, int channelId, string user)
    {
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        if (await db.Channels.Where(c => c.Id == channelId).Select(c => c.CreatedBy).SingleAsync() != user)
            throw new UnauthorizedAccessException("Solo chi ha creato il canale può sceglierne gli agenti.");
    }

    // Contesto che ricevono gli agenti (autorità interna): l'eventuale riassunto della parte vecchia + gli ultimi 30 messaggi.
    public async Task<string> TranscriptAsync(int channelId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var sb = new StringBuilder();
        if (await memory.LatestSummaryAsync(channelId) is { } summary)
            sb.AppendLine("Riassunto della conversazione precedente:").AppendLine(summary.Text).AppendLine().AppendLine("Messaggi recenti:");
        foreach (var m in await LatestAsync(db, channelId, 30)) sb.AppendLine($"[{m.Author}]: {m.Text}");
        return sb.ToString();
    }

    // Per gli strumenti nativi degli agenti (autorità interna, sempre sul canale in cui l'agente lavora).
    public async Task<MessageDto?> MessageAsync(int channelId, long id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Messages.FirstOrDefaultAsync(m => m.Id == id && m.ChannelId == channelId) is { } m ? ToDto(m) : null;
    }

    public async Task<List<MessageDto>> LatestMessagesAsync(int channelId, int take)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await LatestAsync(db, channelId, take);
    }

    public async Task<List<MessageDto>> SearchMessagesAsync(int channelId, string query, int take)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(6).ToList();
        if (words.Count == 0) return [];
        await using var db = await dbf.CreateDbContextAsync();
        var q = db.Messages.Where(m => m.ChannelId == channelId);
        foreach (var w in words) { var pattern = $"%{w.Replace("%", "").Replace("_", "")}%"; q = q.Where(m => EF.Functions.ILike(m.Text, pattern)); }
        return (await q.OrderByDescending(m => m.Id).Take(take).ToListAsync()).AsEnumerable().Reverse().Select(ToDto).ToList();
    }

    public async Task<List<TaskDto>> TasksForAgentsAsync(int channelId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return (await db.Tasks.Where(t => t.ChannelId == channelId).OrderBy(t => t.Id).ToListAsync()).Select(ToDto).ToList();
    }

    // Un agente affida lavoro a un altro agente del canale. Limite ai task aperti creati da agenti: niente catene infinite.
    public async Task<TaskDto?> CreateTaskByAgentAsync(int channelId, string agent, string title, string description, string assignee, int maxOpen)
    {
        title = Required(title, 200, "Titolo");
        description = (description ?? "").Trim();
        if (description.Length > 4000) description = description[..4000];
        var agents = settings.Get().EnabledAgentNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var db = await dbf.CreateDbContextAsync();
        var open = await db.Tasks.CountAsync(t => t.ChannelId == channelId && t.Status != WorkStatus.Done && t.Status != WorkStatus.Failed && t.Status != WorkStatus.Closed);
        var openByAgents = (await db.Tasks.Where(t => t.ChannelId == channelId && t.Status != WorkStatus.Done && t.Status != WorkStatus.Failed && t.Status != WorkStatus.Closed)
            .Select(t => t.CreatedBy).ToListAsync()).Count(agents.Contains);
        if (openByAgents >= maxOpen) return null;
        var t = new TaskEntity
        {
            ChannelId = channelId, Title = title, Description = description, Assignee = assignee, AssigneeIsAgent = true, Agent = assignee,
            Status = WorkStatus.Todo, CreatedBy = agent, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Tasks.Add(t);
        await db.SaveChangesAsync();
        var dto = ToDto(t);
        bus.Publish(new("task", channelId, Task: dto));
        await PostAsync(channelId, "Sistema", AuthorKind.System, $"{agent} ha creato il task #{t.Id} «{title}» per {assignee}.");
        queue.Enqueue(new(channelId, assignee, t.Id, 0));
        return dto;
    }

    // "Crea task dal messaggio": il task punta al messaggio (radice del thread) e lo prende l'agente menzionato,
    // o l'unico agente del canale; senza agenti resta da assegnare. Un messaggio ha al massimo un task.
    public async Task<TaskDto> CreateTaskFromMessageAsync(int channelId, long messageId, string user)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        var m = await db.Messages.FirstOrDefaultAsync(x => x.Id == messageId && x.ChannelId == channelId && x.Kind != AuthorKind.System)
            ?? throw new KeyNotFoundException($"Messaggio {messageId} inesistente.");
        if (m.ParentId is { } root) m = await db.Messages.SingleAsync(x => x.Id == root); // il task è della richiesta, non di una risposta
        if (await db.Tasks.FirstOrDefaultAsync(t => t.MessageId == m.Id) is { } existing) return ToDto(existing);
        var inChannel = await ChannelAgentNamesAsync(channelId);
        var agent = Mentions(m.Text).FirstOrDefault(a => inChannel.Contains(a, StringComparer.OrdinalIgnoreCase)) ?? (inChannel.Count == 1 ? inChannel[0] : null);
        return await AddMessageTaskAsync(db, m, m.Author, agent, null);
    }

    // "Come task": il messaggio non attiva le @menzioni (risponderebbe due volte), il task lo fa partire.
    public async Task<TaskDto> SendAsTaskAsync(int channelId, string user, string text)
    {
        text = Required(text, 4000, "Messaggio");
        await using (var check = await dbf.CreateDbContextAsync()) await ChannelVisibility.EnsureAccessAsync(check, channelId, user);
        var posted = await PostAsync(channelId, user, AuthorKind.User, text, NoMentions);
        return await CreateTaskFromMessageAsync(channelId, posted.Id, user);
    }

    // L'agente ha deciso che la richiesta è un lavoro vero: lo prende come task (chi ha chiesto ne resta l'autore).
    public async Task<TaskDto?> TakeAsTaskAsync(int channelId, string agent, long messageId, string plan)
    {
        await using var db = await dbf.CreateDbContextAsync();
        if (await db.Messages.FirstOrDefaultAsync(x => x.Id == messageId && x.ChannelId == channelId) is not { } m) return null;
        if (m.ParentId is { } root) m = await db.Messages.SingleAsync(x => x.Id == root);
        if (await db.Tasks.AnyAsync(t => t.MessageId == m.Id)) return null;
        return await AddMessageTaskAsync(db, m, m.Author, agent, plan);
    }

    async Task<TaskDto> AddMessageTaskAsync(AppDb db, MessageEntity m, string createdBy, string? agent, string? plan)
    {
        var firstLine = m.Text.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        var t = new TaskEntity
        {
            ChannelId = m.ChannelId, Title = firstLine.Length > 120 ? firstLine[..120] + "…" : firstLine, Description = m.Text,
            Assignee = agent, AssigneeIsAgent = agent is not null, Agent = agent, MessageId = m.Id,
            Status = WorkStatus.Todo, CreatedBy = createdBy, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Tasks.Add(t);
        await db.SaveChangesAsync();
        var dto = ToDto(t);
        bus.Publish(new("task", m.ChannelId, Task: dto));
        if (agent is not null)
        {
            // Conferma nel thread della richiesta: lì l'agente lavora e consegna.
            await PostAsync(m.ChannelId, agent, AuthorKind.Agent, $"Lo prendo io come task #{t.Id}." + (string.IsNullOrWhiteSpace(plan) ? "" : $"\n{plan.Trim()}"), NoMentions, parentId: m.Id);
            queue.Enqueue(new(m.ChannelId, agent, t.Id, 0, m.Id));
        }
        return dto;
    }

    // L'agente ha bisogno di una decisione (o gli è stato chiesto di far controllare il lavoro): il task passa a chi l'ha chiesto.
    public async Task HandToRequesterAsync(int taskId, string note)
    {
        await using var db = await dbf.CreateDbContextAsync();
        if (await db.Tasks.FindAsync(taskId) is not { } t) return;
        var requesterIsPerson = !settings.Get().EnabledAgentNames().Contains(t.CreatedBy, StringComparer.OrdinalIgnoreCase);
        if (requesterIsPerson) { t.Assignee = t.CreatedBy; t.AssigneeIsAgent = false; }
        await SaveStatusAsync(db, t, WorkStatus.Todo, note);
    }

    // Conversazione del task (thread della richiesta), per il prompt dell'agente.
    public async Task<string> ThreadTranscriptAsync(long rootId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var sb = new StringBuilder();
        foreach (var m in await db.Messages.Where(m => m.Id == rootId || m.ParentId == rootId).OrderBy(m => m.Id).Take(100).ToListAsync())
            sb.AppendLine($"[{m.Author}]: {m.Text}");
        return sb.ToString();
    }

    public async Task<List<TaskDto>> GetTasksAsync(int channelId, string user)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        return (await db.Tasks.Where(t => t.ChannelId == channelId).OrderBy(t => t.Id).ToListAsync()).Select(ToDto).ToList();
    }

    public async Task<TaskDto> CreateTaskAsync(int channelId, NewTaskRequest req, string user)
    {
        var title = Required(req.Title, 200, "Titolo");
        var description = (req.Description ?? "").Trim();
        if (description.Length > 4000) throw new ArgumentException("Descrizione: massimo 4000 caratteri.");
        string? assignee = null;
        var isAgent = false;
        if (req.Assignee == NewTaskRequest.Me) assignee = user;
        else if (!string.IsNullOrWhiteSpace(req.Assignee))
        {
            assignee = (await ChannelAgentNamesAsync(channelId)).FirstOrDefault(a => a.Equals(req.Assignee, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"L'agente \"{req.Assignee}\" non è in questo canale (o è disabilitato).");
            isAgent = true;
        }

        await using var db = await dbf.CreateDbContextAsync();
        await ChannelVisibility.EnsureAccessAsync(db, channelId, user);
        var t = new TaskEntity
        {
            ChannelId = channelId, Title = title, Description = description, Assignee = assignee, AssigneeIsAgent = isAgent, Agent = isAgent ? assignee : null,
            Status = WorkStatus.Todo, CreatedBy = user, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Tasks.Add(t);
        await db.SaveChangesAsync();
        var dto = ToDto(t);
        bus.Publish(new("task", channelId, Task: dto));
        await PostAsync(channelId, "Sistema", AuthorKind.System, $"{user} ha creato il task #{t.Id} «{title}»" + (assignee is null ? "." : $" per {assignee}."));
        if (isAgent) queue.Enqueue(new(channelId, assignee!, t.Id, 0));
        return dto;
    }

    // Dall'utente: "Fatto" sui task umani, "Riapri" su qualsiasi task (un task di agente riaperto torna in coda).
    public async Task<List<TaskDto>> GetAllTasksAsync(string user)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var visible = ChannelVisibility.Visible(db, user);
        // shortcut: gli ultimi 500 aggiornati; oltre servirebbe la paginazione lato server.
        return (await db.Tasks.Where(t => visible.Any(c => c.Id == t.ChannelId)).OrderByDescending(t => t.UpdatedAt).Take(500).ToListAsync())
            .Select(ToDto).ToList();
    }

    public async Task SetStatusByUserAsync(int taskId, WorkStatus status, string user)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var t = await db.Tasks.FindAsync(taskId);
        if (t is null || !await ChannelVisibility.CanAccessAsync(db, t.ChannelId, user)) throw new KeyNotFoundException($"Task #{taskId} inesistente.");
        if (status == t.Status) return;
        if (status == WorkStatus.Failed) throw new ArgumentException("Stato non consentito.");
        // Gli agenti gestiscono da soli "In corso" e "Fatto"; mentre ci lavorano il task non si sposta.
        if (t.AssigneeIsAgent && status is WorkStatus.InProgress or WorkStatus.Done) throw new ArgumentException("I task degli agenti si chiudono da soli.");
        if (t.AssigneeIsAgent && t.Status == WorkStatus.InProgress) throw new ArgumentException($"{t.Assignee} ci sta lavorando: aspetta che finisca.");
        // Scrittura condizionata allo stato letto: se nel frattempo il worker l'ha preso (StartTaskAsync), non lo sovrascriviamo.
        var observed = t.Status;
        var result = status == WorkStatus.Todo ? null : t.Result;
        var now = DateTimeOffset.UtcNow;
        var changed = await db.Tasks.Where(x => x.Id == taskId && x.Status == observed)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, status).SetProperty(x => x.Result, result).SetProperty(x => x.UpdatedAt, now));
        if (changed == 0)
            throw new ArgumentException(t.AssigneeIsAgent ? $"{t.Assignee} ci sta lavorando: aspetta che finisca." : "Il task è cambiato nel frattempo: ricarica.");
        t.Status = status; t.Result = result; t.UpdatedAt = now;
        bus.Publish(new("task", t.ChannelId, Task: ToDto(t)));
        var verb = status switch
        {
            WorkStatus.Done => "completato", WorkStatus.Todo => "riaperto", WorkStatus.InProgress => "iniziato",
            WorkStatus.Backlog => "spostato nel backlog", _ => "chiuso",
        };
        await PostAsync(t.ChannelId, "Sistema", AuthorKind.System, $"{user} ha {verb} il task #{t.Id}.");
        if (status == WorkStatus.Todo && t.AssigneeIsAgent) queue.Enqueue(new(t.ChannelId, t.Assignee!, t.Id, 0));
    }

    // Dal worker: prende il task solo se è ancora "Da fare". Aggiornamento condizionale nel DB (atomico): con più lavoratori
    // in parallelo, o più repliche, un solo job lo ottiene anche se il task è finito in coda due volte.
    public async Task<TaskDto?> StartTaskAsync(int taskId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var taken = await db.Tasks.Where(t => t.Id == taskId && t.Status == WorkStatus.Todo)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, WorkStatus.InProgress).SetProperty(t => t.Result, (string?)null).SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow));
        if (taken == 0) return null;
        var dto = ToDto(await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId));
        bus.Publish(new("task", dto.ChannelId, Task: dto));
        return dto;
    }

    public async Task FinishTaskAsync(int taskId, WorkStatus status, string result)
    {
        await using var db = await dbf.CreateDbContextAsync();
        if (await db.Tasks.FindAsync(taskId) is { } t) await SaveStatusAsync(db, t, status, result);
    }

    // All'avvio: i task rimasti "In corso" (server spento a metà) tornano "Da fare" e si rimettono in coda.
    public async Task<List<TaskEntity>> RecoverAgentTasksAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        await db.Tasks.Where(t => t.AssigneeIsAgent && t.Status == WorkStatus.InProgress).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, WorkStatus.Todo));
        return await db.Tasks.Where(t => t.AssigneeIsAgent && t.Status == WorkStatus.Todo).OrderBy(t => t.Id).ToListAsync();
    }

    // Membri di un canale privato (null se pubblico): serve all'hub per mandare gli eventi "channel" solo a loro.
    public async Task<List<string>?> PrivateMembersAsync(int channelId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        if (!await db.Channels.AnyAsync(c => c.Id == channelId && c.IsPrivate)) return null;
        return await db.ChannelMembers.Where(m => m.ChannelId == channelId).Select(m => m.UserName).ToListAsync();
    }

    async Task<TaskDto> SaveStatusAsync(AppDb db, TaskEntity t, WorkStatus status, string? result)
    {
        t.Status = status;
        t.Result = result;
        t.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var dto = ToDto(t);
        bus.Publish(new("task", t.ChannelId, Task: dto));
        return dto;
    }

    static async Task<List<MessageDto>> LatestAsync(AppDb db, int channelId, int take)
    {
        var list = await db.Messages.Where(m => m.ChannelId == channelId).OrderByDescending(m => m.Id).Take(take).ToListAsync();
        return list.AsEnumerable().Reverse().Select(ToDto).ToList();
    }

    List<string> Mentions(string text)
    {
        var agents = settings.Get().EnabledAgentNames().ToList();
        return MentionRegex().Matches(text)
            .Select(m => agents.FirstOrDefault(a => a.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>().Distinct().ToList();
    }

    static string Required(string? value, int max, string field)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0) throw new ArgumentException($"{field}: obbligatorio.");
        if (value.Length > max) throw new ArgumentException($"{field}: massimo {max} caratteri.");
        return value;
    }

    static MessageDto ToDto(MessageEntity m) => new(m.Id, m.ChannelId, m.Author, m.Kind, m.Text, m.At, m.ParentId);
    static TaskDto ToDto(TaskEntity t) => new(t.Id, t.ChannelId, t.Title, t.Description, t.Assignee, t.AssigneeIsAgent, t.Status, t.Result, t.CreatedBy, t.UpdatedAt, t.MessageId, t.Agent);

    [GeneratedRegex(@"@([\p{L}\p{N}_-]+)")]
    private static partial Regex MentionRegex();
}
