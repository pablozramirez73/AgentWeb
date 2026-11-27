using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using static AgentWeb.Shared.Lang;

namespace AgentWeb.Shared.Agents;

// Agente di AgentWeb: nome (da @menzionare), scopo (system prompt), come è raggiungibile e l'LLM che lo fa girare.
public sealed class LocalAgentConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Prompt { get; set; } = ""; // lo scopo dell'agente: diventa il suo system prompt
    public string Protocol { get; set; } = AgentProtocols.Internal;
    public LlmProviderConfig Llm { get; set; } = new();
    // Strumenti consentiti: gruppi nativi (AgentTools) e server MCP come "mcp:<nome>". null = impostazioni precedenti (vedi Normalize).
    public List<string>? Tools { get; set; }

    public bool Can(string tool) => Tools?.Contains(tool, StringComparer.OrdinalIgnoreCase) == true;

    // Diritti sui file della sua cartella sul server (AgentFiles): salvati con l'agente, nello stesso file e nella stessa scrittura.
    public List<FileRule> Files { get; set; } = [];

    // Utente sul cui computer gira l'agente (LLM "daemon"), altrimenti null.
    [JsonIgnore] public string? DaemonOwner => Llm.Kind == LlmKinds.Daemon ? Llm.Url.Trim() : null;

    // Solo per leggere le impostazioni della versione con i provider separati (vedi AgentSettings.Normalize).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Provider { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Model { get; set; }
}

// Diritto di un agente su una parte della sua cartella. Path relativo alla cartella: * = un livello, ** = qualsiasi
// profondità; un percorso senza * vale anche per tutto ciò che contiene (docs = docs e docs/**).
public sealed class FileRule
{
    public string Path { get; set; } = "";
    public bool Read { get; set; }
    public bool Write { get; set; }
    public bool Execute { get; set; }

    public override string ToString() => $"{Path}={(Read ? "r" : "")}{(Write ? "w" : "")}{(Execute ? "x" : "")}";

    // Relativo e dentro la cartella: niente assoluti, "..", \ o : (unità Windows).
    public static bool IsValidPath(string p) =>
        p.Length is > 0 and <= 260 && !p.StartsWith('/') && !p.Contains('\\') && !p.Contains(':') && !p.Any(char.IsControl)
        && p.Split('/').All(s => s != "..");

    // Formato testuale per MCP: "docs/**=r; out/**=rw; run.sh=x".
    public static List<FileRule> ParseList(string text) =>
        [.. text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Parse)];

    static FileRule Parse(string s)
    {
        var eq = s.LastIndexOf('=');
        var flags = eq < 0 ? "" : s[(eq + 1)..].Trim().ToLowerInvariant();
        if (eq <= 0 || flags.Length == 0 || flags.Any(c => c is not ('r' or 'w' or 'x'))) throw new FormatException(T("val.fileRule", s));
        return new() { Path = s[..eq].Trim(), Read = flags.Contains('r'), Write = flags.Contains('w'), Execute = flags.Contains('x') };
    }
}

// Gruppi di strumenti nativi che un agente può usare (si scelgono nella sua scheda, insieme ai server MCP).
public static class AgentTools
{
    public const string Collaborate = "collaborate", ReadChannel = "workspace.read", WriteChannel = "workspace.write",
        Web = "web.fetch", Memory = "memory", McpPrefix = "mcp:";

    // Proprietà, non campi statici: le etichette seguono la lingua scelta all'avvio.
    public static (string Id, string Label, string Help)[] Native =>
    [
        (Collaborate, T("tools.collaborate"), T("tools.collaborate.help")),
        (ReadChannel, T("tools.read"), "read_channel, search_messages, list_tasks."),
        (WriteChannel, T("tools.write"), T("tools.write.help")),
        (Web, T("tools.web"), T("tools.web.help")),
        (Memory, T("tools.memory"), T("tools.memory.help")),
    ];

    public static readonly List<string> Defaults = [Collaborate, ReadChannel, Memory];
}

// Come un agente è raggiungibile. Tra loro gli agenti di AgentWeb collaborano sempre (deleghe con ask_agent e canali).
public static class AgentProtocols
{
    public const string Internal = "internal", A2A = "a2a", Mcp = "mcp";

    public static (string Value, string Label, string Help)[] All =>
    [
        (Internal, T("protocol.internal"), T("protocol.internal.help")),
        (A2A, "A2A", T("protocol.a2a.help")),
        (Mcp, "MCP", T("protocol.mcp.help")),
    ];
}

// Tipi di provider: quelli Ollama differiscono solo per URL e chiave, ma li distinguiamo per guidare chi configura.
public static class LlmKinds
{
    public const string OllamaLocal = "ollama-local", OllamaRemote = "ollama-remote", OllamaCloud = "ollama-cloud",
        OpenAI = "openai", Anthropic = "anthropic",
        Daemon = "daemon"; // sul computer di un utente: Url = l'utente (email), DefaultModel = il runtime del suo daemon

    public static (string Kind, string Label, string DefaultUrl, bool NeedsKey)[] All =>
    [
        (OllamaLocal, T("llmKind.ollamaLocal"), "http://localhost:11434", false),
        (OllamaRemote, T("llmKind.ollamaRemote"), "http://server:11434", false),
        (OllamaCloud, "Ollama Cloud", "https://ollama.com", true),
        (OpenAI, T("llmKind.openai"), "https://api.openai.com/v1", true),
        (Anthropic, "Anthropic (Claude)", "https://api.anthropic.com", true),
        (Daemon, T("llmKind.daemon"), "", false),
    ];

    public static bool IsOllama(string kind) => kind is OllamaLocal or OllamaRemote or OllamaCloud;
}

// Un endpoint LLM. shortcut: la chiave API è salvata in chiaro in agentsettings.json (come i token A2A), letta solo dagli admin.
public sealed class LlmProviderConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = LlmKinds.OllamaLocal;
    public string Url { get; set; } = "http://localhost:11434";
    public string ApiKey { get; set; } = "";
    public string DefaultModel { get; set; } = ""; // il modello scelto (dalla lista caricata con "Carica modelli" o scritto a mano)
}

public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Transport { get; set; } = "http"; // "http" | "stdio"
    public string Url { get; set; } = "";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Headers { get; set; } = ""; // una per riga: "Chiave: Valore"
}

public sealed class A2AAgentConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = ""; // base URL: la card è in /.well-known/agent-card.json
    public string BearerToken { get; set; } = "";
}

public sealed partial class AgentSettings
{
    public List<LocalAgentConfig> LocalAgents { get; set; } =
    [
        new() { Name = T("defaults.architect"), Prompt = T("defaults.architect.prompt") },
        new() { Name = T("defaults.critic"), Prompt = T("defaults.critic.prompt") },
    ];
    public List<McpServerConfig> McpServers { get; set; } = [];
    public List<A2AAgentConfig> A2AAgents { get; set; } = [];

    // Solo lettura delle impostazioni precedenti (provider separati): Normalize li porta dentro gli agenti.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<LlmProviderConfig>? Providers { get; set; }

    // Ogni agente deve avere il suo LLM: dal vecchio provider scelto, altrimenti (solo se è ancora Ollama locale) il modello di default.
    // Un tipo scelto dall'utente non si cambia mai: senza modello resta così e la validazione chiede di sceglierlo.
    public AgentSettings Normalize(string defaultOllamaUrl, string defaultModel)
    {
        foreach (var a in LocalAgents)
        {
            if (string.IsNullOrWhiteSpace(a.Llm.DefaultModel))
            {
                var legacy = Providers?.FirstOrDefault(p => p.Enabled && (string.IsNullOrWhiteSpace(a.Provider) || p.Name.Equals(a.Provider, StringComparison.OrdinalIgnoreCase)));
                if (legacy is not null)
                    a.Llm = new LlmProviderConfig { Kind = legacy.Kind, Url = legacy.Url, ApiKey = legacy.ApiKey, DefaultModel = string.IsNullOrWhiteSpace(a.Model) ? legacy.DefaultModel : a.Model };
                else if (a.Llm.Kind == LlmKinds.OllamaLocal)
                {
                    a.Llm.DefaultModel = string.IsNullOrWhiteSpace(a.Model) ? defaultModel : a.Model;
                    if (a.Llm.Url is "" or "http://localhost:11434") a.Llm.Url = defaultOllamaUrl;
                }
            }
            a.Llm.Name = a.Name;
            // Agenti salvati prima dei permessi: tengono ciò che avevano (collaborazione e tutti i server MCP) più lettura e memoria.
            a.Tools ??= [.. AgentTools.Defaults, .. McpServers.Select(m => AgentTools.McpPrefix + m.Name)];
            if (AgentProtocols.All.All(p => p.Value != a.Protocol)) a.Protocol = AgentProtocols.Internal;
            a.Provider = null;
            a.Model = null;
        }
        Providers = null;
        return this;
    }

    public LocalAgentConfig? FindLocal(string name) =>
        LocalAgents.FirstOrDefault(a => a.Enabled && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public List<string> Validate(bool allowStdio, bool allowExecute = false)
    {
        var errors = new List<string>();
        foreach (var a in LocalAgents.Where(a => a.Enabled))
        {
            var p = a.Llm;
            if (LlmKinds.All.All(k => k.Kind != p.Kind)) errors.Add(T("val.llmKind", a.Name, p.Kind));
            if (p.Kind == LlmKinds.Daemon ? !p.Url.Contains('@') : !IsHttpUrl(p.Url))
                errors.Add(T(p.Kind == LlmKinds.Daemon ? "val.daemonUser" : "val.llmUrl", a.Name));
            if (p.Kind is LlmKinds.OllamaCloud or LlmKinds.Anthropic && string.IsNullOrWhiteSpace(p.ApiKey))
                errors.Add(T("val.apiKey", a.Name));
            if (string.IsNullOrWhiteSpace(p.DefaultModel)) errors.Add(T("val.model", a.Name));
            if (AgentProtocols.All.All(x => x.Value != a.Protocol)) errors.Add(T("val.protocol", a.Name, a.Protocol));
            foreach (var t in a.Tools ?? [])
            {
                if (t.StartsWith(AgentTools.McpPrefix) ? McpServers.All(m => !m.Name.Equals(t[AgentTools.McpPrefix.Length..], StringComparison.OrdinalIgnoreCase))
                                                       : AgentTools.Native.All(n => n.Id != t))
                    errors.Add(T("val.tool", a.Name, t));
            }
            if (a.Files.Count > 0 && a.DaemonOwner is not null) errors.Add(T("val.filesDaemon", a.Name));
            foreach (var r in a.Files)
            {
                if (!FileRule.IsValidPath(r.Path)) errors.Add(T("val.filePath", a.Name, r.Path));
                else if (!(r.Read || r.Write || r.Execute)) errors.Add(T("val.fileFlags", a.Name, r.Path));
                if (r.Execute && !allowExecute) errors.Add(T("val.fileExecute", a.Name, r.Path));
            }
        }
        void Names(IEnumerable<string> names, string kind)
        {
            foreach (var n in names.Where(string.IsNullOrWhiteSpace)) errors.Add(T("val.nameRequired", kind));
            foreach (var d in names.Where(n => !string.IsNullOrWhiteSpace(n)).GroupBy(n => n.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                errors.Add(T("val.nameDuplicate", kind, d.Key));
        }
        Names(McpServers.Select(s => s.Name), "MCP");
        // Agenti locali e A2A condividono i nomi: sono quelli che si @menzionano nei canali.
        Names(LocalAgents.Select(a => a.Name).Concat(A2AAgents.Select(a => a.Name)), T("settings.agents"));
        foreach (var n in LocalAgents.Select(a => a.Name).Concat(A2AAgents.Select(a => a.Name)).Where(n => !string.IsNullOrWhiteSpace(n) && !AgentName().IsMatch(n)))
            errors.Add(T("val.agentName", n, n.Replace(" ", "")));
        foreach (var a in LocalAgents.Where(a => string.IsNullOrWhiteSpace(a.Prompt)))
            errors.Add(T("val.prompt", a.Name));

        foreach (var s in McpServers)
        {
            if (s.Transport == "stdio")
            {
                if (!allowStdio) errors.Add(T("val.stdio", s.Name));
                if (string.IsNullOrWhiteSpace(s.Command)) errors.Add(T("val.command", s.Name));
            }
            else if (!IsHttpUrl(s.Url)) errors.Add(T("val.url", "MCP", s.Name));

            foreach (var line in HeaderLines(s.Headers).Where(l => !l.Contains(':')))
                errors.Add(T("val.header", s.Name, line));
        }
        foreach (var a in A2AAgents.Where(a => !IsHttpUrl(a.Url)))
            errors.Add(T("val.url", "A2A", a.Name));
        return errors;
    }

    public static IEnumerable<string> HeaderLines(string headers) =>
        headers.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IEnumerable<string> EnabledAgentNames() =>
        LocalAgents.Where(a => a.Enabled).Select(a => a.Name).Concat(A2AAgents.Where(a => a.Enabled).Select(a => a.Name));

    [GeneratedRegex(@"^[\p{L}\p{N}_-]+$")]
    private static partial Regex AgentName();

    static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}


public sealed record AgentSettingsView(AgentSettings Settings, bool AllowStdio, bool AllowExecute = false);

// Implementata in locale dal server web e via HTTP dall'app MAUI: le pagine non sanno dove gira la logica.
public interface IAgentSettingsService
{
    Task<AgentSettingsView> GetAsync(CancellationToken ct = default);
    Task SaveAsync(AgentSettings settings, CancellationToken ct = default); // InvalidOperationException se non valida
    Task<string> TestMcpAsync(McpServerConfig server, CancellationToken ct = default);
    Task<string> TestLlmAsync(LlmProviderConfig provider, CancellationToken ct = default); // risposta breve del modello
    Task<List<string>> ListModelsAsync(LlmProviderConfig provider, CancellationToken ct = default); // "Carica modelli"
    Task<string> TestA2AAsync(A2AAgentConfig agent, CancellationToken ct = default);
}
