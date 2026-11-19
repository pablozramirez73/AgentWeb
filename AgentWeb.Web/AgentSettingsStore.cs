using System.Text.Json;
using AgentWeb.Shared.Agents;

namespace AgentWeb.Web;

// shortcut: file JSON in chiaro (token inclusi) nella content root; per produzione passare a user-secrets/Key Vault.
public sealed class AgentSettingsStore(IWebHostEnvironment env, IConfiguration config, DaemonBroker daemons) : IAgentSettingsService
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly string path = config["AgentSettings:Path"] ?? Path.Combine(env.ContentRootPath, "agentsettings.json");
    readonly Lock gate = new();

    public bool AllowStdio => env.IsDevelopment(); // stdio esegue comandi sul server: mai esporlo fuori da Development

    // Restituisce sempre una copia: le modifiche nella UI non toccano la config attiva finché non si salva.
    public AgentSettings Get()
    {
        AgentSettings s;
        lock (gate)
            s = File.Exists(path) ? JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(path)) ?? new() : new();
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, path, overwrite: true); // scrittura atomica: niente file troncato se il processo cade
        }
    }

    public Task<AgentSettingsView> GetAsync(CancellationToken ct = default) => Task.FromResult(new AgentSettingsView(Get(), AllowStdio));

    public Task SaveAsync(AgentSettings settings, CancellationToken ct = default) { Save(settings); return Task.CompletedTask; }

    public Task<string> TestMcpAsync(McpServerConfig server, CancellationToken ct = default) => AgentConnections.TestMcpAsync(server, AllowStdio, ct);

    public Task<string> TestA2AAsync(A2AAgentConfig agent, CancellationToken ct = default) => AgentConnections.TestA2AAsync(agent, ct);

    public Task<string> TestLlmAsync(LlmProviderConfig provider, CancellationToken ct = default) => provider.Kind == LlmKinds.Daemon
        ? Task.FromResult(daemons.Offers(provider.Url.Trim(), provider.DefaultModel)
            ? $"Il daemon di {provider.Url.Trim()} è collegato e offre «{provider.DefaultModel}»."
            : throw new InvalidOperationException($"Nessun daemon di {provider.Url.Trim()} collegato con il runtime «{provider.DefaultModel}»."))
        : LlmClients.TestAsync(provider, ct);

    // Per un daemon i "modelli" sono i runtime che i suoi computer collegati offrono.
    public Task<List<string>> ListModelsAsync(LlmProviderConfig provider, CancellationToken ct = default) =>
        provider.Kind == LlmKinds.Daemon ? Task.FromResult(daemons.Runtimes(provider.Url.Trim())) : LlmClients.ListModelsAsync(provider, ct);
}
