using System.ComponentModel;
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Workspace;
using Microsoft.Extensions.AI;
using AgentWeb.Shared;
using static AgentWeb.Shared.Lang;

namespace AgentWeb.Web;

// Strumenti nativi degli agenti, concessi a gruppi dalla scheda dell'agente (AgentTools). Regole comuni:
// - si agisce solo nel canale in cui l'agente sta lavorando (call.ChannelId), mai in altri canali;
// - i contenuti letti (web, messaggi, memoria) sono dati: il modello lo sa dalle descrizioni e dai prefissi dei risultati.
public sealed class NativeTools(WorkspaceStore ws, MemoryStore memory, SafeFetch web)
{
    public const int MaxTasksPerRun = 3, MaxOpenAgentTasks = 10;

    public IEnumerable<AITool> For(LocalAgentConfig agent, AgentCall call, IReadOnlyCollection<string> team)
    {
        var self = agent.Name;
        var channel = call.ChannelId;

        if (agent.Can(AgentTools.ReadChannel))
        {
            yield return AIFunctionFactory.Create(
                async ([LDescription("tool.read_channel.limit")] int limit = 30) =>
                    Format(await ws.LatestMessagesAsync(channel, Math.Clamp(limit, 1, 100))),
                "read_channel", T("tool.read_channel"));
            yield return AIFunctionFactory.Create(
                async ([LDescription("tool.search_messages.query")] string query) =>
                    await ws.SearchMessagesAsync(channel, query, 20) is { Count: > 0 } found ? Format(found) : T("tool.noMessages"),
                "search_messages", T("tool.search_messages"));
            yield return AIFunctionFactory.Create(
                async () => string.Join("\n", (await ws.TasksForAgentsAsync(channel)).Select(t =>
                    $"#{t.Id} [{t.Status}] {t.Title}{(t.Assignee is null ? "" : $" → {t.Assignee}")}{(t.Result is { Length: > 0 } r ? " | " + T("tool.result", Short(r, 300)) : "")}")) is { Length: > 0 } l ? l : T("tool.noTasks"),
                "list_tasks", T("mcp.list_tasks"));
        }

        if (agent.Can(AgentTools.WriteChannel))
        {
            var created = 0;
            yield return AIFunctionFactory.Create(
                async ([LDescription("mcp.send_message.text")] string text) =>
                {
                    await ws.PostAsync(channel, self, AuthorKind.Agent, text, call.Depth + 1);
                    return T("tool.posted");
                },
                "post_message", T("tool.post_message"));
            yield return AIFunctionFactory.Create(
                async ([LDescription("mcp.create_task.title")] string title,
                       [LDescription("tool.create_task.description")] string description,
                       [LDescription("tool.create_task.assignee")] string assignee) =>
                {
                    if (++created > MaxTasksPerRun) return T("tool.taskLimit", MaxTasksPerRun);
                    var target = team.FirstOrDefault(a => a.Equals(assignee.TrimStart('@'), StringComparison.OrdinalIgnoreCase));
                    if (target is null) return T("tool.notAnAgent", assignee, string.Join(", ", team));
                    if (target.Equals(self, StringComparison.OrdinalIgnoreCase)) return T("tool.selfTask");
                    var t = await ws.CreateTaskByAgentAsync(channel, self, title, description, target, MaxOpenAgentTasks);
                    return t is null ? T("tool.tooManyTasks", MaxOpenAgentTasks) : T("tool.taskCreated", t.Id, target);
                },
                "create_task", T("tool.create_task"));
        }

        if (agent.Can(AgentTools.Web))
        {
            yield return AIFunctionFactory.Create(
                async ([LDescription("tool.fetch_url.url")] string url, CancellationToken ct) => await web.FetchAsync(url, ct),
                "fetch_url", T("tool.fetch_url"));
        }

        if (agent.Can(AgentTools.Memory))
        {
            yield return AIFunctionFactory.Create(
                async ([LDescription("tool.remember.fact")] string fact,
                       [LDescription("tool.remember.scope")] string scope = "canale") =>
                {
                    var agentScope = scope.Trim().StartsWith("agent", StringComparison.OrdinalIgnoreCase);
                    var id = await memory.RememberAsync(agentScope ? "agent" : "channel", agentScope ? self : null, agentScope ? null : channel, fact, self);
                    return T("tool.remembered", id);
                },
                "remember", T("tool.remember"));
            yield return AIFunctionFactory.Create(
                async ([LDescription("tool.recall.query")] string query) =>
                {
                    var hits = await memory.SearchAsync(self, channel, query, 6);
                    return hits.Count == 0 ? T("tool.noMemories") :
                        T("tool.memoriesFound") + "\n" + string.Join("\n", hits.Select(h =>
                            $"#{h.Id} [{(h.Kind == "document" ? T("tool.mem.document", h.Title) : T(h.Kind == "summary" ? "tool.mem.summary" : h.Scope == "agent" ? "tool.mem.yours" : "tool.mem.channel"))}] {Short(h.Content, 700)}"));
                },
                "recall", T("tool.recall"));
            yield return AIFunctionFactory.Create(
                async ([LDescription("tool.forget.id")] long id) =>
                    await memory.ForgetAsync(id, self, channel) ? T("tool.forgotten", id) : T("tool.notYourMemory", id),
                "forget", T("tool.forget"));
        }
    }

    static string Format(IEnumerable<MessageDto> messages) =>
        string.Join("\n", messages.Select(m => $"[{m.At.ToLocalTime():dd/MM HH:mm}] {m.Author}: {Short(m.Text, 1000)}"));

    static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
