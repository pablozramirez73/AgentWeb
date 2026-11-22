using System.Security.Claims;
using System.Text.RegularExpressions;
using AgentWeb.Shared.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AgentWeb.Web;

// Consegna il lavoro degli agenti con LLM "daemon" al computer del loro utente. Il server non esegue nulla:
// mette la richiesta in coda, il daemon (`agentweb daemon`, autenticato come quell'utente) la prende con un long-poll,
// la esegue con un runtime locale e restituisce il testo.
// Almeno una volta: una consegna presa e non più confermata (heartbeat) entro il lease torna in coda; il daemon scarta i doppioni per Id.
// Stato in Postgres (DaemonDeliveries, DaemonMachines): più repliche condividono coda e lease; la presa usa FOR UPDATE SKIP LOCKED.
// shortcut: tra repliche diverse ci si accorge del lavoro nuovo o finito con un controllo al secondo (sulla stessa replica
// subito, con un segnale in memoria); con molti daemon conviene LISTEN/NOTIFY di Postgres.
public sealed partial class DaemonBroker(IConfiguration config, TimeProvider time, IDbContextFactory<AppDb> dbf, ILogger<DaemonBroker> log)
    : BackgroundService
{
    const int MaxAttempts = 3;
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    // Chi aspetta la risposta si fa vedere a ogni tick: oltre questo silenzio la sua replica è caduta e la consegna non serve più.
    static readonly TimeSpan WaiterGrace = TimeSpan.FromSeconds(10);
    readonly TimeSpan lease = TimeSpan.FromSeconds(config.GetValue("Agents:DaemonLeaseSeconds", 30));
    readonly TimeSpan poll = TimeSpan.FromSeconds(config.GetValue("Agents:DaemonPollSeconds", 25));
    readonly TimeSpan timeout = TimeSpan.FromMinutes(config.GetValue("Agents:DaemonTimeoutMinutes", 15.0));
    readonly Lock gate = new();
    TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    DateTimeOffset Now => time.GetUtcNow();
    static string Key(string s) => s.Trim().ToLowerInvariant();

    // Vivo = ha fatto un poll o un heartbeat da poco (un poll dura al massimo "poll", poi il daemon ne fa subito un altro).
    DateTimeOffset AliveSince => Now - poll - lease;
    DateTimeOffset WaiterSince => Now - WaiterGrace;

    IQueryable<DaemonMachineEntity> Alive(AppDb db, string owner)
    {
        var (o, since) = (Key(owner), AliveSince);
        return db.DaemonMachines.Where(m => m.Owner == o && m.LastSeen > since);
    }

    public async Task<bool> OffersAsync(string owner, string runtime)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var r = Key(runtime);
        return await Alive(db, owner).AnyAsync(m => m.Runtimes.Contains(r));
    }

    public async Task<List<string>> RuntimesAsync(string owner)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return (await Alive(db, owner).Select(m => m.Runtimes).ToListAsync()).SelectMany(r => r).Distinct().Order().ToList();
    }

    public async Task<List<MachineDto>> MachinesAsync(string owner)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await Alive(db, owner).OrderBy(m => m.Name)
            .Select(m => new MachineDto(m.Name, m.Runtimes, m.LastSeen, db.DaemonDeliveries.Count(d => d.Sid == m.Sid && !d.Done)))
            .ToListAsync();
    }

    public async Task<string> AskAsync(string owner, string runtime, string prompt, CancellationToken ct)
    {
        if (!await OffersAsync(owner, runtime))
            throw new InvalidOperationException($"nessun daemon di {owner} offre il runtime \"{runtime}\": va avviato `agentweb daemon --runtime {runtime}=<comando>` su quel computer.");
        var id = Guid.NewGuid().ToString("N");
        await using (var db = await dbf.CreateDbContextAsync())
        {
            db.DaemonDeliveries.Add(new() { Id = id, Owner = Key(owner), Runtime = Key(runtime), Prompt = prompt, CreatedAt = Now, WaiterSeenAt = Now });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        Signal();
        var deadline = Now + timeout;
        try
        {
            while (true)
            {
                await WaitAsync(Tick, ct);
                await using var db = await dbf.CreateDbContextAsync();
                var seen = Now;
                await db.DaemonDeliveries.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.WaiterSeenAt, seen), ct);
                var w = await db.DaemonDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
                    ?? throw new InvalidOperationException("consegna al daemon persa.");
                if (w.Done)
                    return w.Error is { Length: > 0 } e ? throw new InvalidOperationException(w.Attempts > MaxAttempts ? e : $"il runtime {w.Runtime} ha fallito: {e}") : w.Result ?? "";
                if (Now > deadline) throw new TimeoutException($"il daemon di {owner} non ha risposto entro {timeout.TotalMinutes:0} minuti.");
                if (w.Sid is null && !await OffersAsync(owner, runtime)) throw new InvalidOperationException($"il daemon di {owner} si è disconnesso.");
            }
        }
        finally
        {
            await using var db = await dbf.CreateDbContextAsync();
            await db.DaemonDeliveries.Where(d => d.Id == id).ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    // Long-poll: restituisce una consegna per i runtime offerti, o null dopo "poll" secondi senza lavoro.
    public async Task<DaemonDelivery?> PollAsync(string owner, string sid, DaemonPollRequest req, CancellationToken ct)
    {
        var (o, runtimes, name) = (Key(owner), req.Runtimes.Select(Key).Distinct().ToArray(), req.Machine.Trim());
        var until = Now + poll;
        // Una volta per poll: resta "vivo" per poll + lease, e il daemon ne fa subito un altro.
        await using (var db = await dbf.CreateDbContextAsync())
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "DaemonMachines" ("Sid", "Owner", "Name", "Runtimes", "LastSeen") VALUES ({sid}, {o}, {name}, {runtimes}, {Now})
                ON CONFLICT ("Sid") DO UPDATE SET "Name" = EXCLUDED."Name", "Runtimes" = EXCLUDED."Runtimes", "LastSeen" = EXCLUDED."LastSeen"
                """, ct);
        while (true)
        {
            await using (var db = await dbf.CreateDbContextAsync())
            {
                var (leaseUntil, waiterSince) = (Now + lease, WaiterSince);
                // Le riconsegne (Attempts > 0) passano per prime; SKIP LOCKED: due daemon non prendono la stessa consegna.
                var taken = await db.DaemonDeliveries.FromSql($"""
                    UPDATE "DaemonDeliveries" SET "Sid" = {sid}, "LeaseUntil" = {leaseUntil}, "Attempts" = "Attempts" + 1
                    WHERE "Id" = (SELECT "Id" FROM "DaemonDeliveries"
                                  WHERE "Owner" = {o} AND "Sid" IS NULL AND NOT "Done" AND "Runtime" = ANY({runtimes}) AND "WaiterSeenAt" > {waiterSince}
                                  ORDER BY "Attempts" DESC, "CreatedAt" LIMIT 1 FOR UPDATE SKIP LOCKED)
                    RETURNING *
                    """).AsNoTracking().ToListAsync(ct);
                if (taken is [var w]) return new(w.Id, w.Runtime, w.Prompt);
            }
            var left = until - Now;
            if (left <= TimeSpan.Zero) return null;
            await WaitAsync(left < Tick ? left : Tick, ct);
        }
    }

    // Il daemon lavora ancora alla consegna: rinnova il lease. False = non è più sua (scaduta, annullata): fermi il processo.
    public async Task<bool> HeartbeatAsync(string sid, string id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var (now, leaseUntil) = (Now, Now + lease);
        await db.DaemonMachines.Where(m => m.Sid == sid).ExecuteUpdateAsync(s => s.SetProperty(m => m.LastSeen, now));
        return await db.DaemonDeliveries.Where(d => d.Id == id && d.Sid == sid && !d.Done)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LeaseUntil, leaseUntil)) > 0;
    }

    // Una sola volta: un secondo Complete della stessa consegna non cambia la risposta già data (404).
    public async Task<bool> CompleteAsync(string sid, string id, DaemonResult r)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var (text, error) = (r.Error is { Length: > 0 } ? null : (r.Text ?? "").Trim(), r.Error is { Length: > 0 } e ? e : null);
        var done = await db.DaemonDeliveries.Where(d => d.Id == id && d.Sid == sid && !d.Done)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Done, true).SetProperty(d => d.Result, text).SetProperty(d => d.Error, error)) > 0;
        if (done) Signal();
        return done;
    }

    // Un solo timer per replica (non in ogni poll/attesa): lease/2, al massimo 5 s di ritardo sulla scadenza.
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromTicks(Math.Min(lease.Ticks / 2, TimeSpan.FromSeconds(5).Ticks)), time);
        while (await timer.WaitForNextTickAsync(ct))
            try { await ExpireAsync(ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning(e, "scadenza delle consegne daemon fallita"); }
    }

    // Lease scaduti: in coda di nuovo, o falliti dopo MaxAttempts. Pulizia di ciò che nessuno aspetta più (es. dopo un riavvio).
    async Task ExpireAsync(CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var now = Now;
        var requeued = await db.DaemonDeliveries.Where(d => d.Sid != null && !d.Done && d.LeaseUntil < now && d.Attempts < MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Sid, (string?)null), ct);
        await db.DaemonDeliveries.Where(d => d.Sid != null && !d.Done && d.LeaseUntil < now && d.Attempts >= MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Done, true).SetProperty(d => d.Attempts, MaxAttempts + 1)
                .SetProperty(d => d.Error, $"consegna abbandonata {MaxAttempts} volte dal daemon."), ct);
        // Senza più chi aspetta (replica caduta): via subito, così nessun daemon la esegue e chi la sta eseguendo si ferma (heartbeat false).
        var (stale, orphan) = (now - timeout - lease, WaiterSince);
        await db.DaemonDeliveries.Where(d => d.CreatedAt < stale || d.WaiterSeenAt < orphan).ExecuteDeleteAsync(ct);
        var gone = now - TimeSpan.FromHours(1);
        await db.DaemonMachines.Where(m => m.LastSeen < gone).ExecuteDeleteAsync(ct);
        if (requeued > 0) Signal();
    }

    Task WaitAsync(TimeSpan max, CancellationToken ct)
    {
        Task signal;
        lock (gate) signal = changed.Task;
        return signal.WaitAsync(max, time, ct).ContinueWith(t => { if (t.IsCanceled) ct.ThrowIfCancellationRequested(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    void Signal()
    {
        TaskCompletionSource old;
        lock (gate) { old = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        old.TrySetResult();
    }

    [GeneratedRegex(@"^[\p{L}\p{N}_.-]{1,50}$")]
    internal static partial Regex RuntimeName();
}

public static class DaemonEndpoints
{
    // /api/daemon: le usa solo `agentweb daemon`, con il login dell'utente (nessun privilegio in più: riceve solo il lavoro
    // degli agenti che un admin ha legato a quell'utente, nei canali che quell'utente vede — vedi AgentRuntime).
    public static void MapDaemonEndpoints(this WebApplication app)
    {
        var d = app.MapGroup("/api/daemon").RequireAuthorization();
        d.MapPost("/poll", async (DaemonPollRequest req, DaemonBroker b, ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (req.Machine is not { Length: > 0 and <= 100 } || req.Runtimes is not { Count: > 0 and <= 20 } || !req.Runtimes.All(DaemonBroker.RuntimeName().IsMatch))
                return Results.Text("Macchina (max 100 caratteri) e da 1 a 20 runtime (lettere, numeri, _ . -) obbligatori.", statusCode: 400);
            if (Sid(u) is not { } sid) return Results.Unauthorized();
            try { return await b.PollAsync(u.Identity!.Name!, sid, req, ct) is { } w ? Results.Ok(w) : Results.NoContent(); }
            catch (OperationCanceledException) { return Results.NoContent(); } // il daemon ha chiuso la connessione
        });
        d.MapPost("/deliveries/{id}/heartbeat", async (string id, DaemonBroker b, ClaimsPrincipal u) =>
            Sid(u) is { } sid && await b.HeartbeatAsync(sid, id) ? Results.NoContent() : Results.NotFound());
        d.MapPost("/deliveries/{id}/complete", async (string id, DaemonResult r, DaemonBroker b, ClaimsPrincipal u) =>
            Sid(u) is { } sid && await b.CompleteAsync(sid, id, r) ? Results.NoContent() : Results.NotFound());
        d.MapGet("/machines", (DaemonBroker b, ClaimsPrincipal u) => b.MachinesAsync(u.Identity!.Name!));
    }

    static string? Sid(ClaimsPrincipal u) => u.FindFirstValue(SessionStore.Claim);
}

// LLM "daemon": il modello gira sul computer dell'utente indicato (Url) con il runtime indicato (DefaultModel).
// Niente strumenti di AgentWeb: il runtime locale ha i suoi. La conversazione diventa un unico prompt di testo.
public sealed class DaemonChatClient(DaemonBroker daemons, string owner, string runtime) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        new(new ChatMessage(ChatRole.Assistant, await daemons.AskAsync(owner, runtime, Prompt(messages), cancellationToken)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return u;
    }

    public static string Prompt(IEnumerable<ChatMessage> messages)
    {
        var list = messages.Where(m => !string.IsNullOrWhiteSpace(m.Text)).ToList();
        if (list is [var s, var u] && s.Role == ChatRole.System && u.Role == ChatRole.User) return s.Text + "\n\n" + u.Text;
        return string.Join("\n\n", list.Select(m => $"[{m.Role.Value}{(m.AuthorName is { } a ? " " + a : "")}]\n{m.Text}"));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
