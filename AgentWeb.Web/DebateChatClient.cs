using System.Runtime.CompilerServices;
using System.Text;
using A2A;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

using AgentWeb.Shared.Agents;

using static AgentWeb.Shared.Lang;

namespace AgentWeb.Web;

// Orchestra il dibattito in un unico stream: ogni intervento è un messaggio con MessageId e AuthorName propri,
// così UIAgent lo rende come blocco separato attribuito all'agente giusto.
// Partecipanti: gli agenti locali abilitati (ognuno col suo provider LLM e i tool MCP) + gli agenti A2A remoti abilitati.
public sealed class DebateChatClient(LlmClients llms, AgentSettingsStore store, int rounds = 2) : IChatClient
{
    sealed record Participant(string Name, string? Prompt, IA2AClient? Remote, IChatClient? Llm = null, List<AITool>? Tools = null);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = store.Get();
        // Un collegamento per server MCP; ogni partecipante riceve solo gli strumenti dei server che la sua scheda consente.
        var locals = settings.LocalAgents.Where(a => a.Enabled).ToList();
        var needed = settings.McpServers.Where(m => locals.Any(a => a.Can(AgentTools.McpPrefix + m.Name)));
        var (byServer, mcpClients, notices) = await AgentConnections.ConnectMcpByServerAsync(needed, store.AllowStdio, cancellationToken);
        try
        {
            var participants = locals.Select(a => new Participant(a.Name, a.Prompt, null, llms.For(a),
                byServer.Where(kv => a.Can(AgentTools.McpPrefix + kv.Key)).SelectMany(kv => kv.Value).ToList())).ToList();
            foreach (var a in settings.A2AAgents.Where(a => a.Enabled))
            {
                try { participants.Add(new Participant(a.Name, null, (await AgentConnections.ConnectA2AAsync(a, cancellationToken)).Client)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { notices.Add(T("debate.a2aUnavailable", a.Name, ex.Message)); }
            }

            if (notices.Count > 0)
            {
                yield return Say("Sistema", "sistema", string.Join("\n", notices));
                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, AuthorName = "Sistema", FinishReason = ChatFinishReason.Stop };
            }

            var transcript = new StringBuilder();
            foreach (var m in messages)
                transcript.AppendLine($"[{m.AuthorName ?? T(m.Role == ChatRole.User ? "field.user" : "field.agent")}]: {m.Text}");

            for (int i = 0; i < rounds * participants.Count; i++)
            {
                var p = participants[i % participants.Count];
                var messageId = Guid.NewGuid().ToString("N");
                var request = T("debate.prompt", transcript, p.Name);
                var reply = new StringBuilder();

                if (p.Remote is null)
                {
                    List<ChatMessage> turn = [new(ChatRole.System, p.Prompt), new(ChatRole.User, request)];
                    await foreach (var update in p.Llm!.GetStreamingResponseAsync(turn, p.Tools is { Count: > 0 } t ? new ChatOptions { Tools = t } : null, cancellationToken))
                    {
                        reply.Append(update.Text);
                        update.AuthorName = p.Name;
                        if (update.Role == ChatRole.Assistant) update.MessageId = messageId;
                        yield return update;
                    }
                }
                else
                {
                    string text;
                    try { text = await AgentConnections.AskA2AAsync(p.Remote, request, cancellationToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { text = T("debate.a2aError", ex.Message); }
                    reply.Append(text);
                    yield return Say(p.Name, messageId, text);
                }
                // UIAgent (preview) unisce testi consecutivi anche con MessageId diversi: FinishReason chiude il blocco.
                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, AuthorName = p.Name, MessageId = messageId, FinishReason = ChatFinishReason.Stop };
                transcript.AppendLine($"[{p.Name}]: {reply}");
            }
        }
        finally
        {
            foreach (var c in mcpClients) await c.DisposeAsync();
        }
    }

    static ChatResponseUpdate Say(string author, string messageId, string text) =>
        new(ChatRole.Assistant, text) { AuthorName = author, MessageId = messageId };

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { } // i client LLM sono condivisi (LlmClients): non si chiudono qui
}
