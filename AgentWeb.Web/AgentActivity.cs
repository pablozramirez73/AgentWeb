using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentWeb.Web;

// Cosa stanno facendo gli agenti: lavori in corso e ultimi completati (pagina Attività), più la telemetria
// OpenTelemetry dello stesso punto: uno span per lavoro, contatore ed istogramma della durata, numero in corso.
// shortcut: in memoria e per replica (con più repliche ognuna mostra i suoi lavori); per lo storico c'è Grafana/Prometheus.
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

    public AgentActivity() => Meter.CreateObservableGauge("agentweb.agent.running", () => running.Count, description: "Lavori degli agenti in corso");

    public async Task TrackAsync(AgentJob job, Func<Task> work)
    {
        var kind = job.TaskId is null ? "risposta" : "task";
        var run = new Run(Guid.NewGuid(), job.Agent, job.ChannelId, kind, DateTimeOffset.UtcNow);
        using var span = Source.StartActivity($"agent {kind}");
        span?.SetTag("agentweb.agent", job.Agent);
        span?.SetTag("agentweb.channel", job.ChannelId);
        if (job.TaskId is { } t) span?.SetTag("agentweb.task", t);
        running[run.Id] = run;
        var watch = Stopwatch.StartNew();
        string? error = null;
        try { await work(); }
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
            recent.Enqueue(run with { Seconds = seconds, Error = error });
            while (recent.Count > Keep) recent.TryDequeue(out _);
            KeyValuePair<string, object?>[] tags = [new("agent", job.Agent), new("kind", kind), new("outcome", error is null ? "ok" : "error")];
            Runs.Add(1, tags);
            Duration.Record(seconds, tags);
        }
    }

    public (List<Run> Running, List<Run> Recent) Snapshot() =>
        (running.Values.OrderBy(r => r.StartedAt).ToList(), recent.Reverse().ToList());
}
