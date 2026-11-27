using System.ClientModel;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Anthropic;
using Anthropic.Core;
using AgentWeb.Shared.Agents;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

using static AgentWeb.Shared.Lang;

namespace AgentWeb.Web;

// Un IChatClient per provider configurato (Ollama locale/remoto/Cloud, compatibile OpenAI, Anthropic).
// I client si riusano finché la configurazione del provider non cambia; i tool MCP li esegue UseFunctionInvocation.
// Parallelismo: più agenti lavorano insieme; per ogni server LLM al massimo Agents:MaxParallelPerLlm chiamate contemporanee
// (default: numero di core), così un Ollama locale non viene sommerso. Se un modello chiede più strumenti nello stesso turno
// (es. ask_agent verso tre agenti) le chiamate partono in parallelo.
public sealed class LlmClients(IConfiguration config, DaemonBroker daemons)
{
    // Timeout lungo: un modello locale caricato a freddo può metterci minuti.
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    // ...ma la connessione no: un host irraggiungibile risponde (o non risponde) in pochi secondi, non deve sembrare un modello che carica.
    static readonly SocketsHttpHandler Net = new() { ConnectTimeout = TimeSpan.FromSeconds(8), PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    // Errori di rete in parole utili: il server gira sul VPS, quindi "localhost" e la rete di casa non sono il computer di chi configura.
    public static Exception Explain(Exception e, LlmProviderConfig p)
    {
        if (!Uri.TryCreate(p.Url.Trim(), UriKind.Absolute, out var uri)) return new InvalidOperationException(T("llm.badUrl", p.Url));
        var local = uri.IsLoopback || uri.Host is "0.0.0.0" or "host.docker.internal";
        var onYourComputer = T("llm.onYourComputer");
        var socket = (e as HttpRequestException)?.InnerException as System.Net.Sockets.SocketException;
        return socket?.SocketErrorCode switch
        {
            System.Net.Sockets.SocketError.ConnectionRefused when local => new InvalidOperationException(
                T("llm.refusedLocal", uri.Host, onYourComputer)),
            System.Net.Sockets.SocketError.ConnectionRefused => new InvalidOperationException(
                T("llm.refused", uri.Host, uri.Port)),
            System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData => new InvalidOperationException(
                T("llm.hostNotFound", uri.Host)),
            _ when e is TaskCanceledException or TimeoutException || e.InnerException is TimeoutException => new InvalidOperationException(
                T("llm.timeout", uri.Host, uri.Port, onYourComputer)),
            _ => e,
        };
    }
    readonly ConcurrentDictionary<string, IChatClient> cache = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> perServer = new();
    readonly int maxPerServer = Math.Max(1, config.GetValue("Agents:MaxParallelPerLlm", Environment.ProcessorCount));

    public IChatClient For(LlmProviderConfig p, string? model = null)
    {
        var m = string.IsNullOrWhiteSpace(model) ? p.DefaultModel : model.Trim();
        var key = string.Join('\u0001', p.Kind, p.Url.Trim(), p.ApiKey, m);
        if (p.Kind == LlmKinds.Daemon) return cache.GetOrAdd(key, _ => new DaemonChatClient(daemons, p.Url.Trim(), m)); // niente tool né limite: decide il daemon
        var gate = perServer.GetOrAdd(p.Kind + "|" + p.Url.Trim().TrimEnd('/'), _ => new SemaphoreSlim(maxPerServer, maxPerServer));
        return cache.GetOrAdd(key, _ => new UsageChatClient(new ThrottledChatClient(Create(p, m), gate), m)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: AgentActivity.Name) // span gen_ai e token usati per modello (visibili solo con OpenTelemetry attivo)
            .UseFunctionInvocation(configure: f => f.AllowConcurrentInvocation = true).Build());
    }

    public IChatClient For(LocalAgentConfig agent) => For(agent.Llm);

    // "Carica modelli": l'elenco dei modelli che il server LLM espone, con l'URL e il token inseriti (anche non ancora salvati).
    public static async Task<List<string>> ListModelsAsync(LlmProviderConfig p, CancellationToken ct)
    {
        var url = p.Url.Trim().TrimEnd('/');
        var key = string.IsNullOrWhiteSpace(p.ApiKey) ? null : p.ApiKey.Trim();
        using var http = new HttpClient(Net, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        HttpRequestMessage req;
        if (LlmKinds.IsOllama(p.Kind)) req = new(HttpMethod.Get, url + "/api/tags");
        else if (p.Kind == LlmKinds.OpenAI) req = new(HttpMethod.Get, url + "/models");
        else if (p.Kind == LlmKinds.Anthropic)
        {
            req = new(HttpMethod.Get, url + "/v1/models?limit=1000");
            req.Headers.Add("anthropic-version", "2023-06-01");
            if (key is not null) req.Headers.Add("x-api-key", key);
        }
        else throw new InvalidOperationException(T("llm.unknownKind", p.Kind));
        if (key is not null && p.Kind != LlmKinds.Anthropic) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var r = await http.SendAsync(req, ct);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException(r.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? T("llm.badToken") : T("llm.http", (int)r.StatusCode));
        using var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        // Ollama: {"models":[{"name":...}]}; OpenAI e Anthropic: {"data":[{"id":...}]}
        var items = LlmKinds.IsOllama(p.Kind) ? doc.RootElement.GetProperty("models") : doc.RootElement.GetProperty("data");
        var field = LlmKinds.IsOllama(p.Kind) ? "name" : "id";
        return items.EnumerateArray().Select(m => m.GetProperty(field).GetString()!).Where(n => n.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Prova rapida dalle Impostazioni: una risposta di poche parole, senza tool.
    public static async Task<string> TestAsync(LlmProviderConfig p, CancellationToken ct)
    {
        using var client = Create(p, p.DefaultModel);
        var r = await client.GetResponseAsync([new(ChatRole.User, T("llm.pingPrompt"))], new ChatOptions { MaxOutputTokens = 1024 } /* i modelli che "pensano" (es. gemma4) spendono token nel ragionamento prima di rispondere */, ct);
        var text = r.Text.Trim();
        return T("llm.answers", p.DefaultModel, text.Length > 80 ? text[..80] + "…" : text.Length == 0 ? T("llm.empty") : text);
    }

    static IChatClient Create(LlmProviderConfig p, string model)
    {
        var url = p.Url.Trim().TrimEnd('/');
        var key = string.IsNullOrWhiteSpace(p.ApiKey) ? null : p.ApiKey.Trim();
        switch (p.Kind)
        {
            case LlmKinds.OllamaLocal or LlmKinds.OllamaRemote or LlmKinds.OllamaCloud:
                var http = new HttpClient(Net, disposeHandler: false) { BaseAddress = new Uri(url + "/"), Timeout = Timeout };
                // Ollama Cloud (https://ollama.com) e i server remoti protetti vogliono la chiave come Bearer.
                if (key is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
                return new OllamaApiClient(http, model);

            case LlmKinds.OpenAI:
                // Qualsiasi server con il protocollo OpenAI (/v1/chat/completions): l'URL deve includere /v1.
                var options = new OpenAIClientOptions { Endpoint = new Uri(url), NetworkTimeout = Timeout };
                return new OpenAIClient(new ApiKeyCredential(key ?? "nessuna-chiave"), options).GetChatClient(model).AsIChatClient();

            case LlmKinds.Anthropic:
                var anthropic = new AnthropicClient(new ClientOptions { ApiKey = key, BaseUrl = url, Timeout = Timeout });
                return anthropic.AsIChatClient(model, defaultMaxOutputTokens: 2048);

            default:
                throw new InvalidOperationException(T("llm.unknownKind", p.Kind));
        }
    }
}

// Limita le chiamate contemporanee a un server LLM. Sta sotto UseFunctionInvocation: il posto si occupa solo durante la
// richiesta HTTP al modello, non mentre si eseguono gli strumenti (deleghe comprese), quindi niente blocchi reciproci.
sealed class ThrottledChatClient(IChatClient inner, SemaphoreSlim gate) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await base.GetResponseAsync(messages, options, cancellationToken); }
        finally { gate.Release(); }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)) yield return update;
        }
        finally { gate.Release(); }
    }
}

// Somma i token di ogni chiamata al modello nel lavoro in corso (AgentActivity.Current), per le Statistiche.
sealed class UsageChatClient(IChatClient inner, string model) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var r = await base.GetResponseAsync(messages, options, cancellationToken);
        AgentActivity.Current.Value?.Add(r.Usage, r.ModelId ?? model);
        return r;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var u in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (var c in u.Contents) if (c is UsageContent usage) AgentActivity.Current.Value?.Add(usage.Details, u.ModelId ?? model);
            yield return u;
        }
    }
}
