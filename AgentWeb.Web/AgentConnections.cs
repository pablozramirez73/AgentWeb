using System.Net.Http.Headers;
using A2A;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

using AgentWeb.Shared.Agents;

namespace AgentWeb.Web;

public static class AgentConnections
{
    // Un handler condiviso: un HttpClient per agente (header diversi) senza esaurire i socket.
    static readonly SocketsHttpHandler SharedHandler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    public static Task<McpClient> ConnectMcpAsync(McpServerConfig s, bool allowStdio, CancellationToken ct = default)
    {
        IClientTransport transport;
        if (s.Transport == "stdio")
        {
            if (!allowStdio) throw new InvalidOperationException("Trasporto stdio consentito solo in Development.");
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = s.Name,
                Command = s.Command,
                // shortcut: split sugli spazi, niente quoting; se servono argomenti con spazi usare una lista.
                Arguments = s.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            });
        }
        else
        {
            transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = s.Name,
                Endpoint = new Uri(s.Url),
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                AdditionalHeaders = AgentSettings.HeaderLines(s.Headers)
                    .Select(l => l.Split(':', 2))
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim()),
            });
        }
        return McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    // Connette tutti i server MCP abilitati; quelli irraggiungibili finiscono in Notices invece di bloccare l'agente.
    // Il chiamante deve chiudere i Clients quando ha finito di usare i tool.
    // servers: quelli che l'agente può usare (permessi "mcp:<nome>" nella sua scheda).
    public static async Task<(List<AITool> Tools, List<McpClient> Clients, List<string> Notices)> ConnectMcpToolsAsync(
        IEnumerable<McpServerConfig> servers, bool allowStdio, CancellationToken ct)
    {
        var (byServer, clients, notices) = await ConnectMcpByServerAsync(servers, allowStdio, ct);
        return (byServer.Values.SelectMany(t => t).ToList(), clients, notices);
    }

    // Come sopra, ma con gli strumenti divisi per server (nel dibattito ogni partecipante riceve solo i suoi).
    // Tutti i server in parallelo: il tempo di avvio è quello del più lento, non la somma.
    public static async Task<(Dictionary<string, List<AITool>> ByServer, List<McpClient> Clients, List<string> Notices)> ConnectMcpByServerAsync(
        IEnumerable<McpServerConfig> servers, bool allowStdio, CancellationToken ct)
    {
        var attempts = await Task.WhenAll(servers.Where(s => s.Enabled).Select(async s =>
        {
            try
            {
                var client = await ConnectMcpAsync(s, allowStdio, ct);
                try { return (s.Name, Client: (McpClient?)client, Tools: (await client.ListToolsAsync(cancellationToken: ct)).Cast<AITool>().ToList(), Notice: (string?)null); }
                catch { await client.DisposeAsync(); throw; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (s.Name, Client: (McpClient?)null, Tools: new List<AITool>(), Notice: $"MCP \"{s.Name}\" non disponibile: {ex.Message}");
            }
        }));
        return (attempts.ToDictionary(a => a.Name, a => a.Tools, StringComparer.OrdinalIgnoreCase),
                attempts.Where(a => a.Client is not null).Select(a => a.Client!).ToList(),
                attempts.Where(a => a.Notice is not null).Select(a => a.Notice!).ToList());
    }

    public static async Task<string> TestMcpAsync(McpServerConfig s, bool allowStdio, CancellationToken ct = default)
    {
        await using var client = await ConnectMcpAsync(s, allowStdio, ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return tools.Count == 0 ? "Connesso, nessun tool." : $"Connesso, {tools.Count} tool: {string.Join(", ", tools.Select(t => t.Name))}";
    }

    public static async Task<(AgentCard Card, IA2AClient Client)> ConnectA2AAsync(A2AAgentConfig a, CancellationToken ct = default)
    {
        var http = new HttpClient(SharedHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(2) };
        if (!string.IsNullOrWhiteSpace(a.BearerToken))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", a.BearerToken.Trim());
        // Lo "/" finale conta: senza, ".well-known/agent-card.json" sostituirebbe l'ultimo segmento (es. /a2a/Nome).
        var card = await new A2ACardResolver(new Uri(a.Url.TrimEnd('/') + "/"), http).GetAgentCardAsync(ct);
        return (card, A2AClientFactory.Create(card, http));
    }

    public static async Task<string> TestA2AAsync(A2AAgentConfig a, CancellationToken ct = default)
    {
        var (card, _) = await ConnectA2AAsync(a, ct);
        var skills = card.Skills is { Count: > 0 } ? string.Join(", ", card.Skills.Select(s => s.Name)) : "nessuna skill";
        return $"{card.Name} v{card.Version}: {card.Description} ({skills})";
    }

    public static async Task<string> AskA2AAsync(IA2AClient client, string text, CancellationToken ct = default)
    {
        var response = await client.SendMessageAsync(new SendMessageRequest
        {
            Message = new Message { MessageId = Guid.NewGuid().ToString("N"), Role = Role.User, Parts = [Part.FromText(text)] },
        }, ct);

        if (response.PayloadCase == SendMessageResponseCase.Message)
            return TextOf(response.Message!.Parts);

        // SendMessage è bloccante di default: torna a stato terminale o interrotto (input/auth richiesti).
        var task = response.Task!;
        var status = task.Status.Message is { } m ? TextOf(m.Parts) : "";
        return task.Status.State switch
        {
            TaskState.Completed => string.Join("\n", (task.Artifacts ?? []).Select(art => TextOf(art.Parts))) is { Length: > 0 } output ? output : status,
            // shortcut: la risposta dell'utente non continua lo stesso task A2A (niente contextId/taskId salvati); serve se gli agenti remoti fanno domande spesso.
            TaskState.InputRequired => $"Mi serve un chiarimento: {status}",
            TaskState.AuthRequired => throw new InvalidOperationException($"l'agente remoto chiede autenticazione: {status}"),
            var s => throw new InvalidOperationException($"task A2A {task.Id} in stato {s}{(status.Length > 0 ? ": " + status : "")}"),
        };
    }

    static string TextOf(IEnumerable<Part>? parts) =>
        string.Join("", (parts ?? []).Select(p => p.Text).Where(t => !string.IsNullOrEmpty(t)));
}
