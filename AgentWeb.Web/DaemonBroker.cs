using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AgentWeb.Shared.Workspace;
using Microsoft.Extensions.AI;

namespace AgentWeb.Web;

// Consegna il lavoro degli agenti con LLM "daemon" al computer del loro utente. Il server non esegue nulla:
// mette la richiesta in coda, il daemon (`agentweb daemon`, autenticato come quell'utente) la prende con un long-poll,
// la esegue con un runtime locale e restituisce il testo.
// Almeno una volta: una consegna presa e non più confermata (heartbeat) entro il lease torna in coda; il daemon scarta i doppioni per Id.
// shortcut: coda in memoria, una replica; un riavvio del server perde le risposte alle menzioni in attesa
// (i task tornano in coda con RecoverAgentTasksAsync). Con più repliche servirebbe una tabella delle consegne.
public sealed partial class DaemonBroker(IConfiguration config, TimeProvider time)
{
    sealed class Work(string owner, string runtime, string prompt)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Owner { get; } = owner;
        public string Runtime { get; } = runtime;
        public string Prompt { get; } = prompt;
        public TaskCompletionSource<string> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Sid;
        public DateTimeOffset LeaseUntil;
        public int Attempts;
    }

    sealed record Machine(string Owner, string Name, List<string> Runtimes, DateTimeOffset LastSeen);

    const int MaxAttempts = 3;
    readonly TimeSpan lease = TimeSpan.FromSeconds(config.GetValue("Agents:DaemonLeaseSeconds", 30));
    readonly TimeSpan poll = TimeSpan.FromSeconds(config.GetValue("Agents:DaemonPollSeconds", 25));
    readonly TimeSpan timeout = TimeSpan.FromMinutes(config.GetValue("Agents:DaemonTimeoutMinutes", 15.0));
    readonly Lock gate = new();
    readonly List<Work> queue = [];
    readonly Dictionary<string, Work> byId = [];
    readonly ConcurrentDictionary<string, Machine> machines = new(); // per sessione del daemon (sid)
    TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    DateTimeOffset Now => time.GetUtcNow();
    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    // Vivo = ha fatto un poll o un heartbeat da poco (un poll dura al massimo "poll", poi il daemon ne fa subito un altro).
    bool Alive(Machine m) => Now - m.LastSeen < poll + lease;

    public bool Offers(string owner, string runtime) =>
        machines.Values.Any(m => Same(m.Owner, owner) && Alive(m) && m.Runtimes.Contains(runtime, StringComparer.OrdinalIgnoreCase));

    public List<string> Runtimes(string owner) =>
        machines.Values.Where(m => Same(m.Owner, owner) && Alive(m)).SelectMany(m => m.Runtimes).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    public List<MachineDto> Machines(string owner)
    {
        lock (gate)
            return machines.Where(kv => Same(kv.Value.Owner, owner) && Alive(kv.Value))
                .Select(kv => new MachineDto(kv.Value.Name, kv.Value.Runtimes, kv.Value.LastSeen, byId.Values.Count(w => w.Sid == kv.Key)))
                .OrderBy(m => m.Machine).ToList();
    }

    public async Task<string> AskAsync(string owner, string runtime, string prompt, CancellationToken ct)
    {
        if (!Offers(owner, runtime))
            throw new InvalidOperationException($"nessun daemon di {owner} offre il runtime \"{runtime}\": va avviato `agentweb daemon --runtime {runtime}=<comando>` su quel computer.");
        var w = new Work(owner, runtime, prompt);
        lock (gate) { queue.Add(w); byId[w.Id] = w; Signal(); }
        var deadline = Now + timeout;
        try
        {
            while (true)
            {
                var left = deadline - Now;
                if (left <= TimeSpan.Zero) throw new TimeoutException($"il daemon di {owner} non ha risposto entro {timeout.TotalMinutes:0} minuti.");
                try { return await w.Done.Task.WaitAsync(left < lease ? left : lease, time, ct); }
                catch (TimeoutException) { }
                // Ogni lease: le consegne abbandonate tornano in coda; se nessun daemon è rimasto, inutile aspettare.
                lock (gate) Expire();
                if (w.Sid is null && !Offers(owner, runtime)) throw new InvalidOperationException($"il daemon di {owner} si è disconnesso.");
            }
        }
        finally { lock (gate) { queue.Remove(w); byId.Remove(w.Id); } }
    }

    // Long-poll: restituisce una consegna per i runtime offerti, o null dopo "poll" secondi senza lavoro.
    public async Task<DaemonDelivery?> PollAsync(string owner, string sid, DaemonPollRequest req, CancellationToken ct)
    {
        var runtimes = req.Runtimes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var until = Now + poll;
        while (true)
        {
            machines[sid] = new(owner, req.Machine.Trim(), runtimes, Now);
            Task wait;
            lock (gate)
            {
                Expire();
                if (queue.FirstOrDefault(x => Same(x.Owner, owner) && runtimes.Contains(x.Runtime, StringComparer.OrdinalIgnoreCase)) is { } w)
                {
                    queue.Remove(w);
                    w.Sid = sid;
                    w.LeaseUntil = Now + lease;
                    w.Attempts++;
                    return new(w.Id, w.Runtime, w.Prompt);
                }
                wait = changed.Task;
            }
            var left = until - Now;
            if (left <= TimeSpan.Zero) return null;
            try { await wait.WaitAsync(left, time, ct); }
            catch (TimeoutException) { return null; }
        }
    }

    // Il daemon lavora ancora alla consegna: rinnova il lease. False = non è più sua (scaduta, annullata): fermi il processo.
    public bool Heartbeat(string sid, string id)
    {
        lock (gate)
        {
            if (!byId.TryGetValue(id, out var w) || w.Sid != sid) return false;
            w.LeaseUntil = Now + lease;
            if (machines.TryGetValue(sid, out var m)) machines[sid] = m with { LastSeen = Now };
            return true;
        }
    }

    // Idempotente: un secondo Complete della stessa consegna non cambia la risposta già data.
    public bool Complete(string sid, string id, DaemonResult r)
    {
        lock (gate)
        {
            if (!byId.TryGetValue(id, out var w) || w.Sid != sid) return false;
            if (r.Error is { Length: > 0 } e) w.Done.TrySetException(new InvalidOperationException($"il runtime {w.Runtime} ha fallito: {e}"));
            else w.Done.TrySetResult((r.Text ?? "").Trim());
            return true;
        }
    }

    void Expire()
    {
        foreach (var w in byId.Values.Where(w => w.Sid is not null && w.LeaseUntil < Now).ToList())
        {
            w.Sid = null;
            if (w.Attempts >= MaxAttempts) { w.Done.TrySetException(new InvalidOperationException($"consegna abbandonata {MaxAttempts} volte dal daemon.")); continue; }
            queue.Insert(0, w); // riconsegna per prima
            Signal();
        }
    }

    void Signal()
    {
        var old = changed;
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        d.MapPost("/deliveries/{id}/heartbeat", (string id, DaemonBroker b, ClaimsPrincipal u) =>
            Sid(u) is { } sid && b.Heartbeat(sid, id) ? Results.NoContent() : Results.NotFound());
        d.MapPost("/deliveries/{id}/complete", (string id, DaemonResult r, DaemonBroker b, ClaimsPrincipal u) =>
            Sid(u) is { } sid && b.Complete(sid, id, r) ? Results.NoContent() : Results.NotFound());
        d.MapGet("/machines", (DaemonBroker b, ClaimsPrincipal u) => b.Machines(u.Identity!.Name!));
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
