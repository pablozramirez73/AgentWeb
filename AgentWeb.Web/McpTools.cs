using System.ComponentModel;
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using AgentWeb.Shared;
using static AgentWeb.Shared.Lang;

namespace AgentWeb.Web;

// Server MCP di AgentWeb (/mcp): Claude, Hermes e qualsiasi client MCP lavorano nel workspace come l'utente del token.
// Ogni tool passa da WorkspaceStore con quell'utente: stesse regole di web, app e CLI (canali privati compresi).
[McpServerToolType]
public sealed class AgentWebMcpTools(WorkspaceStore store, WorkspaceBus bus, AgentSettingsStore settings, IHttpContextAccessor http)
{
    string User => http.HttpContext?.User.Identity?.Name ?? throw new McpException(T("mcp.err.unauthenticated"));

    [McpServerTool(Name = "list_channels", ReadOnly = true), LDescription("mcp.list_channels")]
    public Task<List<ChannelDto>> ListChannels() => Run(() => store.GetChannelsAsync(User));

    [McpServerTool(Name = "read_messages", ReadOnly = true), LDescription("mcp.read_messages")]
    public Task<List<MessageDto>> ReadMessages(
        [LDescription("mcp.channel")] string channel,
        [LDescription("mcp.read_messages.since_id")] long? since_id = null) => Run(async () =>
    {
        var id = await ChannelAsync(channel);
        if (since_id is not { } since) return await store.GetMessagesAsync(id, User);
        var replay = await store.GetMessagesSinceAsync(id, User, since);
        return replay.Reset ? await store.GetMessagesAsync(id, User) : replay.Messages;
    });

    [McpServerTool(Name = "send_message"), LDescription("mcp.send_message")]
    public Task<MessageDto> SendMessage([LDescription("mcp.channel")] string channel, [LDescription("mcp.send_message.text")] string text) =>
        Run(async () => await store.PostUserMessageAsync(await ChannelAsync(channel), User, text));

    [McpServerTool(Name = "list_tasks", ReadOnly = true), LDescription("mcp.list_tasks")]
    public Task<List<TaskDto>> ListTasks([LDescription("mcp.channel")] string channel) =>
        Run(async () => await store.GetTasksAsync(await ChannelAsync(channel), User));

    [McpServerTool(Name = "create_task"), LDescription("mcp.create_task")]
    public Task<TaskDto> CreateTask(
        [LDescription("mcp.channel")] string channel,
        [LDescription("mcp.create_task.title")] string title,
        [LDescription("mcp.create_task.description")] string? description = null,
        [LDescription("mcp.create_task.assignee")] string? assignee = null) =>
        Run(async () => await store.CreateTaskAsync(await ChannelAsync(channel), new NewTaskRequest(title, description ?? "", assignee == "me" ? NewTaskRequest.Me : assignee), User));

    [McpServerTool(Name = "set_task_status"), LDescription("mcp.set_task_status")]
    public Task<string> SetTaskStatus([LDescription("mcp.set_task_status.task_id")] int task_id, [LDescription("mcp.set_task_status.status")] string status) => Run(async () =>
    {
        var s = status.Trim().ToLowerInvariant() switch
        {
            "done" => WorkStatus.Done,
            "todo" => WorkStatus.Todo,
            _ => throw new McpException(T("mcp.err.status")),
        };
        await store.SetStatusByUserAsync(task_id, s, User);
        return $"Task #{task_id}: {status}.";
    });

    // Solo gli agenti con protocollo "MCP" sono raggiungibili da qui (gli altri restano interni o su A2A).
    List<LocalAgentConfig> McpAgents() => settings.Get().LocalAgents.Where(a => a.Enabled && a.Protocol == AgentProtocols.Mcp).ToList();

    [McpServerTool(Name = "list_agents", ReadOnly = true), LDescription("mcp.list_agents")]
    public List<string> ListAgents() => McpAgents().Select(a => $"{a.Name}: {a.Prompt}").ToList();

    [McpServerTool(Name = "ask_agent"), LDescription("mcp.ask_agent")]
    public Task<string> AskAgent(
        [LDescription("mcp.ask_agent.agent")] string agent,
        [LDescription("mcp.ask_agent.channel")] string channel,
        [LDescription("mcp.ask_agent.question")] string question,
        [LDescription("mcp.ask_agent.timeout_seconds")] int timeout_seconds = 180) => Run(async () =>
    {
        var name = McpAgents().Select(a => a.Name).FirstOrDefault(a => a.Equals(agent.TrimStart('@'), StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException(T("mcp.err.agentNotMcp", agent));
        var id = await ChannelAsync(channel);
        if (!(await store.ChannelAgentNamesAsync(id)).Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new McpException(T("mcp.err.agentNotInChannel", name, channel));
        long after = 0; // diventa l'id della domanda appena scritta: le risposte arrivano sempre dopo
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(WorkspaceEvent e)
        {
            if (e is not { Kind: "message", Message: { } m } || m.ChannelId != id || m.Id <= Volatile.Read(ref after)) return;
            if (m.Kind == AuthorKind.Agent && m.Author.Equals(name, StringComparison.OrdinalIgnoreCase)) reply.TrySetResult(m.Text);
            else if (m.Kind == AuthorKind.System && m.Text.StartsWith(T("agent.couldNotAnswer", name, "").TrimEnd(' ', ':'), StringComparison.OrdinalIgnoreCase))
                reply.TrySetException(new McpException(m.Text));
        }
        bus.Published += OnEvent; // prima di scrivere: la risposta non può arrivare prima che ascoltiamo
        try
        {
            var asked = await store.PostUserMessageAsync(id, User, $"@{name} {question}");
            Volatile.Write(ref after, asked.Id);
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(timeout_seconds, 5, 900)));
        }
        catch (TimeoutException)
        {
            return T("mcp.notYetAnswered", name);
        }
        finally { bus.Published -= OnEvent; }
    });

    [McpServerTool(Name = "create_channel"), LDescription("mcp.create_channel")]
    public Task<ChannelDto> CreateChannel(
        [LDescription("mcp.create_channel.name")] string name,
        [LDescription("mcp.create_channel.private")] bool @private = false) =>
        Run(() => store.CreateChannelAsync(name, User, @private));

    [McpServerTool(Name = "add_agent_to_channel"), LDescription("mcp.add_agent_to_channel")]
    public Task<string> AddAgentToChannel([LDescription("mcp.add_agent_to_channel.agent")] string agent, [LDescription("mcp.channel")] string channel) => Run(async () =>
    {
        await store.AddChannelAgentAsync(await ChannelAsync(channel), User, agent);
        return T("mcp.agentAdded", agent.TrimStart('@'), channel);
    });

    // Solo amministratori, e niente URL o chiavi via MCP: il nuovo agente usa l'LLM (server e token) di un agente già configurato.
    [McpServerTool(Name = "create_agent"), LDescription("mcp.create_agent")]
    public Task<string> CreateAgent(
        [LDescription("mcp.create_agent.name")] string name,
        [LDescription("mcp.create_agent.purpose")] string purpose,
        [LDescription("mcp.create_agent.like")] string? like = null,
        [LDescription("mcp.create_agent.model")] string? model = null,
        [LDescription("mcp.create_agent.protocol")] string protocol = AgentProtocols.Internal,
        [LDescription("mcp.create_agent.channel")] string? channel = null,
        [LDescription("mcp.create_agent.files")] string? files = null) => Run(async () =>
    {
        RequireAdmin("mcp.admin.createAgents");
        var s = settings.Get();
        var source = (like is null ? s.LocalAgents.FirstOrDefault(a => a.Enabled) : s.FindLocal(like.TrimStart('@')))
            ?? throw new McpException(like is null ? T("mcp.err.noSourceAgent") : T("ws.agentNotFound", like));
        if (AgentProtocols.All.All(x => x.Value != protocol)) throw new McpException(T("mcp.err.protocol"));
        var src = source.Llm;
        s.LocalAgents.Add(new()
        {
            Name = name.Trim().TrimStart('@'), Prompt = purpose.Trim(), Protocol = protocol, Tools = [.. AgentTools.Defaults], Files = Rules(files ?? ""),
            Llm = new() { Kind = src.Kind, Url = src.Url, ApiKey = src.ApiKey, DefaultModel = string.IsNullOrWhiteSpace(model) ? src.DefaultModel : model.Trim() },
        });
        SaveSettings(s);
        var created = s.LocalAgents[^1];
        if (channel is not null) await store.AddChannelAgentAsync(await ChannelAsync(channel), User, created.Name);
        return channel is null ? T("mcp.agentCreated", created.Name, source.Name, created.Llm.DefaultModel)
            : T("mcp.agentCreatedIn", created.Name, source.Name, created.Llm.DefaultModel, channel);
    });

    [McpServerTool(Name = "add_member"), LDescription("mcp.add_member")]
    public Task<string> AddMember([LDescription("mcp.channel")] string channel, [LDescription("mcp.add_member.user")] string user) => Run(async () =>
    {
        await store.AddMemberAsync(await ChannelAsync(channel), User, user.Trim());
        return T("mcp.memberAdded", user.Trim(), channel);
    });

    [McpServerTool(Name = "remove_member"), LDescription("mcp.remove_member")]
    public Task<string> RemoveMember([LDescription("mcp.channel")] string channel, [LDescription("mcp.remove_member.user")] string user) => Run(async () =>
    {
        await store.RemoveMemberAsync(await ChannelAsync(channel), User, user.Trim());
        return T("mcp.memberRemoved", user.Trim(), channel);
    });

    // what: chiave i18n dell'azione.
    void RequireAdmin(string what)
    {
        if (http.HttpContext?.User.IsInRole(AccountEndpoints.AdminRole) != true) throw new McpException(T("mcp.err.adminOnly", T(what)));
    }

    // Come create_agent: niente server o chiavi dell'LLM via MCP, solo ciò che si sceglie nella scheda dell'agente.
    [McpServerTool(Name = "update_agent"), LDescription("mcp.update_agent")]
    public Task<string> UpdateAgent(
        [LDescription("mcp.update_agent.name")] string name,
        [LDescription("mcp.update_agent.purpose")] string? purpose = null,
        [LDescription("mcp.update_agent.model")] string? model = null,
        [LDescription("mcp.update_agent.protocol")] string? protocol = null,
        [LDescription("mcp.update_agent.enabled")] bool? enabled = null,
        [LDescription("mcp.update_agent.files")] string? files = null) => Run(async () =>
    {
        RequireAdmin("mcp.admin.updateAgents");
        var s = settings.Get();
        var a = s.LocalAgents.FirstOrDefault(x => x.Name.Equals(name.TrimStart('@'), StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException(T("mcp.err.agentMissing", name));
        if (protocol is not null && AgentProtocols.All.All(x => x.Value != protocol)) throw new McpException(T("mcp.err.protocol"));
        if (purpose is not null) a.Prompt = purpose.Trim();
        if (!string.IsNullOrWhiteSpace(model)) a.Llm.DefaultModel = model.Trim();
        if (protocol is not null) a.Protocol = protocol;
        if (enabled is { } e) a.Enabled = e;
        if (files is not null) a.Files = Rules(files);
        SaveSettings(s);
        await Task.CompletedTask;
        return T("mcp.agentUpdated", a.Name, a.Llm.DefaultModel, a.Protocol, T(a.Enabled ? "mcp.enabled" : "mcp.disabled"));
    });

    [McpServerTool(Name = "add_a2a_agent"), LDescription("mcp.add_a2a_agent")]
    public Task<string> AddA2AAgent(
        [LDescription("mcp.add_a2a_agent.name")] string name,
        [LDescription("mcp.add_a2a_agent.url")] string url,
        [LDescription("mcp.add_a2a_agent.token")] string? token = null) => Run(async () =>
    {
        RequireAdmin("mcp.admin.connectA2A");
        var peer = new A2AAgentConfig { Name = name.Trim().TrimStart('@'), Url = url.Trim(), BearerToken = token?.Trim() ?? "" };
        string card;
        try { card = await settings.TestA2AAsync(peer).WaitAsync(TimeSpan.FromSeconds(30)); } // prima di salvare: un URL sbagliato si vede subito
        catch (Exception e) { throw new McpException(T("mcp.err.cardUnreachable", peer.Url, e.GetBaseException().Message)); }
        var s = settings.Get();
        s.A2AAgents.Add(peer);
        SaveSettings(s);
        return T("mcp.a2aConnected", peer.Name, card);
    });

    [McpServerTool(Name = "remove_a2a_agent"), LDescription("mcp.remove_a2a_agent")]
    public Task<string> RemoveA2AAgent([LDescription("mcp.remove_a2a_agent.name")] string name) => Run(async () =>
    {
        RequireAdmin("mcp.admin.disconnectA2A");
        var s = settings.Get();
        if (s.A2AAgents.RemoveAll(a => a.Name.Equals(name.TrimStart('@'), StringComparison.OrdinalIgnoreCase)) == 0)
            throw new McpException(T("mcp.err.noA2A", name));
        SaveSettings(s);
        await Task.CompletedTask;
        return T("mcp.a2aDisconnected", name.TrimStart('@'));
    });

    static List<FileRule> Rules(string text)
    {
        try { return FileRule.ParseList(text); }
        catch (FormatException ex) { throw new McpException(ex.Message); }
    }

    // shortcut: legge e riscrive le impostazioni intere; un salvataggio contemporaneo dalla pagina Agenti può sovrascriverlo.
    void SaveSettings(AgentSettings s)
    {
        if (s.Validate(settings.AllowStdio, settings.AllowExecute) is { Count: > 0 } errors) throw new McpException(string.Join(" ", errors));
        settings.Save(s);
    }

    // Documenti: la memoria del canale che gli agenti consultano (stessi permessi della pagina Memoria del canale).
    [McpServerTool(Name = "list_documents", ReadOnly = true), LDescription("mcp.list_documents")]
    public Task<List<string>> ListDocuments([LDescription("mcp.channel")] string channel) => Run(async () =>
        (await store.GetChannelMemoryAsync(await ChannelAsync(channel), User)).Where(m => m.Kind == "document")
            .Select(d => T("mcp.documentLine", d.SourceId, d.Title, d.CreatedBy, d.CreatedAt.ToString("yyyy-MM-dd"), d.Chunks)).ToList());

    [McpServerTool(Name = "upload_document"), LDescription("mcp.upload_document")]
    public Task<string> UploadDocument(
        [LDescription("mcp.channel")] string channel,
        [LDescription("mcp.upload_document.title")] string title,
        [LDescription("mcp.upload_document.text")] string text) => Run(async () =>
    {
        var parts = await store.UploadDocumentAsync(await ChannelAsync(channel), User, title, text);
        return T("mcp.documentUploaded", title, parts);
    });

    [McpServerTool(Name = "search_documents", ReadOnly = true), LDescription("mcp.search_documents")]
    public Task<List<string>> SearchDocuments(
        [LDescription("mcp.channel")] string channel,
        [LDescription("mcp.search_documents.query")] string query,
        [LDescription("mcp.search_documents.limit")] int limit = 5) => Run(async () =>
        (await store.SearchChannelMemoryAsync(await ChannelAsync(channel), User, query, Math.Clamp(limit, 1, 20)))
            .Select(h => $"[{h.Title ?? h.Kind}] {h.Content}").ToList());

    [McpServerTool(Name = "delete_document"), LDescription("mcp.delete_document")]
    public Task<string> DeleteDocument([LDescription("mcp.channel")] string channel, [LDescription("mcp.delete_document.document")] string document) => Run(async () =>
    {
        var id = await ChannelAsync(channel);
        var docs = (await store.GetChannelMemoryAsync(id, User)).Where(m => m.Kind == "document").ToList();
        var matches = docs.Where(d => d.SourceId == document).ToList() is { Count: > 0 } byId ? byId
            : docs.Where(d => string.Equals(d.Title, document, StringComparison.OrdinalIgnoreCase)).ToList();
        var doc = matches switch
        {
            [var one] => one,
            [] => throw new McpException(T("mcp.err.noDocument", document)),
            _ => throw new McpException(T("mcp.err.manyDocuments", document)),
        };
        await store.DeleteChannelMemoryAsync(id, User, doc.Id, doc.SourceId);
        return T("mcp.documentDeleted", doc.Title);
    });

    async Task<int> ChannelAsync(string channel)
    {
        if (int.TryParse(channel, out var id)) return id;
        var name = channel.TrimStart('#');
        return (await store.GetChannelsAsync(User)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new McpException(T("cli.channelNotFound", channel));
    }

    // Errori del dominio come errori MCP leggibili dal modello (le altre eccezioni il SDK le nasconde).
    static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (KeyNotFoundException ex) { throw new McpException(ex.Message); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
        catch (UnauthorizedAccessException ex) { throw new McpException(ex.Message); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }
}
