using System.Text.Json;
using AgentWeb.Shared.Agents;

namespace AgentWeb.Web;

// shortcut: file JSON in chiaro (token inclusi) nella content root; per produzione passare a user-secrets/Key Vault.
public sealed class AgentSettingsStore(IWebHostEnvironment env, IConfiguration config, DaemonBroker daemons, AgentIcons icons) : IAgentSettingsService
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly string path = config["AgentSettings:Path"] ?? Path.Combine(env.ContentRootPath, "agentsettings.json");
    readonly Lock gate = new();
    string? cached; // testo del file: rileggerlo a ogni job serializzava gli agenti in parallelo sul lock
    DateTime cachedAt; // ora di modifica del file letto: se cambia (altra replica, modifica a mano) si rilegge

    string? Text()
    {
        lock (gate)
        {
            var stamp = File.GetLastWriteTimeUtc(path); // stat economico; file assente = data fissa del 1601
            if (cached is null || stamp != cachedAt) { cached = File.Exists(path) ? File.ReadAllText(path) : null; cachedAt = stamp; }
            return cached;
        }
    }

    public bool AllowStdio => env.IsDevelopment(); // stdio esegue comandi sul server: mai esporlo fuori da Development

    // Restituisce sempre una copia: le modifiche nella UI non toccano la config attiva finché non si salva.
    public AgentSettings Get()
    {
        var s = Text() is { } text ? JsonSerializer.Deserialize<AgentSettings>(text) ?? new() : new(); // copia nuova a ogni chiamata
        // Ogni agente ha il suo LLM; le impostazioni precedenti (provider separati o solo Ollama) vengono convertite.
        s.Normalize(config["Ollama:Url"] ?? "http://localhost:11434", config["Ollama:Model"] ?? "gemma4:e4b-mlx");
        return s;
    }

    public void Save(AgentSettings settings)
    {
        var errors = settings.Validate(AllowStdio);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        lock (gate)
        {
            var tmp = path + ".tmp";
            var text = JsonSerializer.Serialize(settings, Json);
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true); // scrittura atomica: niente file troncato se il processo cade
            cached = text;
            cachedAt = File.GetLastWriteTimeUtc(path);
        }
        icons.For(settings.LocalAgents.Where(a => a.Enabled)); // agenti nuovi: l'icona si disegna subito, in background
    }

    public Task<AgentSettingsView> GetAsync(CancellationToken ct = default) => Task.FromResult(new AgentSettingsView(Get(), AllowStdio));

    public Task SaveAsync(AgentSettings settings, CancellationToken ct = default) { Save(settings); return Task.CompletedTask; }

    public Task<string> TestMcpAsync(McpServerConfig server, CancellationToken ct = default) => AgentConnections.TestMcpAsync(server, AllowStdio, ct);

    public Task<string> TestA2AAsync(A2AAgentConfig agent, CancellationToken ct = default) => AgentConnections.TestA2AAsync(agent, ct);

    public async Task<string> TestLlmAsync(LlmProviderConfig provider, CancellationToken ct = default) => provider.Kind != LlmKinds.Daemon
        ? await Explained(() => LlmClients.TestAsync(provider, ct), provider, ct)
        : await daemons.OffersAsync(provider.Url.Trim(), provider.DefaultModel)
            ? $"Il daemon di {provider.Url.Trim()} è collegato e offre «{provider.DefaultModel}»."
            : throw new InvalidOperationException($"Nessun daemon di {provider.Url.Trim()} collegato con il runtime «{provider.DefaultModel}».");

    // Per un daemon i "modelli" sono i runtime che i suoi computer collegati offrono.
    public Task<List<string>> ListModelsAsync(LlmProviderConfig provider, CancellationToken ct = default) =>
        provider.Kind == LlmKinds.Daemon ? daemons.RuntimesAsync(provider.Url.Trim()) : Explained(() => LlmClients.ListModelsAsync(provider, ct), provider, ct);

    static async Task<T> Explained<T>(Func<Task<T>> call, LlmProviderConfig provider, CancellationToken ct)
    {
        try { return await call(); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or TimeoutException)
        { throw LlmClients.Explain(e, provider); }
    }
}
