using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AgentWeb.Web;

// Cosa stanno facendo gli agenti: lavori in corso e ultimi completati (pagina Attività), più la telemetria
// OpenTelemetry dello stesso punto: uno span per lavoro, contatore ed istogramma della durata, numero in corso.
// shortcut: "in corso" e "recenti" sono in memoria e per replica (con più repliche ognuna mostra i suoi); lo storico per le
// Statistiche (durata, esito, token) va invece nel DB, tabella AgentRuns.
public sealed class AgentActivity
{
    public const string Name = "AgentWeb";
    public static readonly ActivitySource Source = new(Name);
    static readonly Meter Meter = new(Name);
    static readonly Counter<long> Runs = Meter.CreateCounter<long>("agentweb.agent.runs", description: "Lavori degli agenti terminati (risposte e task)");
    static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("agentweb.agent.duration", "s", "Durata di un lavoro di un agente");

    public sealed record Run(Guid Id, string Agent, int ChannelId, string Kind, DateTimeOffset StartedAt, double? Seconds = null, string? Error = null);

    const int Keep = 100;
    readonly ConcurrentDictionary<Guid, Run> running = new();
    readonly ConcurrentQueue<Run> recent = new();

    readonly IDbContextFactory<AppDb> dbf;
    readonly ILogger<AgentActivity> log;

    public AgentActivity(IDbContextFactory<AppDb> dbf, ILogger<AgentActivity> log)
    {
        this.dbf = dbf; this.log = log;
        Meter.CreateObservableGauge("agentweb.agent.running", () => running.Count, description: "Lavori degli agenti in corso");
    }

    // Token del lavoro in corso: UsageChatClient ci somma ogni risposta del modello fatta dentro TrackAsync (anche dagli strumenti).
    public sealed class Usage
    {
        long input, output;
        public long Input => Interlocked.Read(ref input);
        public long Output => Interlocked.Read(ref output);
        public string? Model { get; set; }
        public void Add(UsageDetails? d, string model)
        {
            if (d is null) return;
            Interlocked.Add(ref input, d.InputTokenCount ?? 0);
            Interlocked.Add(ref output, d.OutputTokenCount ?? 0);
            Model = model;
        }
    }
    public static readonly AsyncLocal<Usage?> Current = new();

    // kind: "risposta" o "task" dal job; "delega" quando un agente lavora per un altro (ask_agent), con token e modello suoi.
    // ct: il token del lavoro. Se è lui ad annullarlo (server che si ferma, redeploy) non è un errore e non va nello storico;
    // un'OperationCanceledException senza ct annullato (es. timeout HTTP verso l'LLM) resta un errore vero.
    public async Task TrackAsync(AgentJob job, Func<Task> work, string? kind = null, CancellationToken ct = default)
    {
        kind ??= job.TaskId is null ? "risposta" : "task";
        var run = new Run(Guid.NewGuid(), job.Agent, job.ChannelId, kind, DateTimeOffset.UtcNow);
        using var span = Source.StartActivity($"agent {kind}");
        span?.SetTag("agentweb.agent", job.Agent);
        span?.SetTag("agentweb.channel", job.ChannelId);
        if (job.TaskId is { } t) span?.SetTag("agentweb.task", t);
        running[run.Id] = run;
        var usage = Current.Value = new Usage();
        var watch = Stopwatch.StartNew();
        string? error = null;
        var cancelled = false;
        try { await work(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; throw; }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            span?.SetStatus(ActivityStatusCode.Error, error);
            throw;
        }
        finally
        {
            running.TryRemove(run.Id, out _);
            var seconds = watch.Elapsed.TotalSeconds;
            if (!cancelled)
            {
                await SaveAsync(run, seconds, error, usage);
                recent.Enqueue(run with { Seconds = seconds, Error = error });
                while (recent.Count > Keep) recent.TryDequeue(out _);
            }
            KeyValuePair<string, object?>[] tags = [new("agent", job.Agent), new("kind", kind), new("outcome", cancelled ? "cancelled" : error is null ? "ok" : "error")];
            Runs.Add(1, tags);
            Duration.Record(seconds, tags);
        }
    }

    // Lo storico non deve mai far fallire il lavoro: un errore di salvataggio finisce nel log.
    async Task SaveAsync(Run run, double seconds, string? error, Usage usage)
    {
        try
        {
            await using var db = await dbf.CreateDbContextAsync();
            db.AgentRuns.Add(new AgentRunEntity
            {
                Agent = run.Agent, ChannelId = run.ChannelId, Kind = run.Kind, StartedAt = run.StartedAt, Seconds = seconds,
                Error = error is { Length: > 2000 } ? error[..2000] : error, Model = usage.Model, InputTokens = usage.Input, OutputTokens = usage.Output,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { log.LogWarning(ex, "Storico del lavoro di {Agent} non salvato", run.Agent); }
    }

    public (List<Run> Running, List<Run> Recent) Snapshot() =>
        (running.Values.OrderBy(r => r.StartedAt).ToList(), recent.Reverse().ToList());
}
