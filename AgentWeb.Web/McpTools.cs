using System.ComponentModel;
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentWeb.Web;

// Server MCP di AgentWeb (/mcp): Claude, Hermes e qualsiasi client MCP lavorano nel workspace come l'utente del token.
// Ogni tool passa da WorkspaceStore con quell'utente: stesse regole di web, app e CLI (canali privati compresi).
[McpServerToolType]
public sealed class AgentWebMcpTools(WorkspaceStore store, WorkspaceBus bus, AgentSettingsStore settings, IHttpContextAccessor http)
{
    string User => http.HttpContext?.User.Identity?.Name ?? throw new McpException("Non autenticato.");

    [McpServerTool(Name = "list_channels", ReadOnly = true), Description("Elenca i canali del workspace AgentWeb visibili all'utente (id, nome, privato).")]
    public Task<List<ChannelDto>> ListChannels() => Run(() => store.GetChannelsAsync(User));

    [McpServerTool(Name = "read_messages", ReadOnly = true), Description("Legge i messaggi di un canale (gli ultimi 200, o solo quelli successivi a since_id).")]
    public Task<List<MessageDto>> ReadMessages(
        [Description("Id o nome del canale")] string channel,
        [Description("Restituisce solo i messaggi con id maggiore di questo (facoltativo)")] long? since_id = null) => Run(async () =>
    {
        var id = await ChannelAsync(channel);
        if (since_id is not { } since) return await store.GetMessagesAsync(id, User);
        var replay = await store.GetMessagesSinceAsync(id, User, since);
        return replay.Reset ? await store.GetMessagesAsync(id, User) : replay.Messages;
    });

    [McpServerTool(Name = "send_message"), Description("Scrive un messaggio in un canale. Per coinvolgere un agente AgentWeb scrivi @NomeAgente nel testo.")]
    public Task<MessageDto> SendMessage([Description("Id o nome del canale")] string channel, [Description("Testo del messaggio")] string text) =>
        Run(async () => await store.PostUserMessageAsync(await ChannelAsync(channel), User, text));

    [McpServerTool(Name = "list_tasks", ReadOnly = true), Description("Elenca i task di un canale con stato, assegnatario e risultato.")]
    public Task<List<TaskDto>> ListTasks([Description("Id o nome del canale")] string channel) =>
        Run(async () => await store.GetTasksAsync(await ChannelAsync(channel), User));

    [McpServerTool(Name = "create_task"), Description("Crea un task in un canale. Se assegnato a un agente AgentWeb, l'agente lo svolge e scrive il risultato nel canale.")]
    public Task<TaskDto> CreateTask(
        [Description("Id o nome del canale")] string channel,
        [Description("Titolo del task")] string title,
        [Description("Descrizione (facoltativa)")] string? description = null,
        [Description("Nome di un agente AgentWeb, oppure 'me' per l'utente (facoltativo)")] string? assignee = null) =>
        Run(async () => await store.CreateTaskAsync(await ChannelAsync(channel), new NewTaskRequest(title, description ?? "", assignee == "me" ? NewTaskRequest.Me : assignee), User));

    [McpServerTool(Name = "set_task_status"), Description("Segna un task come fatto ('done') o lo riapre ('todo'). I task degli agenti si chiudono da soli.")]
    public Task<string> SetTaskStatus([Description("Id del task")] int task_id, [Description("'done' oppure 'todo'")] string status) => Run(async () =>
    {
        var s = status.Trim().ToLowerInvariant() switch
        {
            "done" => WorkStatus.Done,
            "todo" => WorkStatus.Todo,
            _ => throw new McpException("status deve essere 'done' o 'todo'."),
        };
        await store.SetStatusByUserAsync(task_id, s, User);
        return $"Task #{task_id}: {status}.";
    });

    // Solo gli agenti con protocollo "MCP" sono raggiungibili da qui (gli altri restano interni o su A2A).
    List<LocalAgentConfig> McpAgents() => settings.Get().LocalAgents.Where(a => a.Enabled && a.Protocol == AgentProtocols.Mcp).ToList();

    [McpServerTool(Name = "list_agents", ReadOnly = true), Description("Elenca gli agenti AgentWeb raggiungibili via MCP, con il loro scopo.")]
    public List<string> ListAgents() => McpAgents().Select(a => $"{a.Name}: {a.Prompt}").ToList();

    [McpServerTool(Name = "ask_agent"), Description("Fa una domanda a un agente AgentWeb in un canale e aspetta la sua risposta (scrive '@agente domanda' come l'utente).")]
    public Task<string> AskAgent(
        [Description("Nome dell'agente (vedi list_agents)")] string agent,
        [Description("Id o nome del canale dove porre la domanda")] string channel,
        [Description("La domanda o la richiesta")] string question,
        [Description("Secondi massimi di attesa (default 180)")] int timeout_seconds = 180) => Run(async () =>
    {
        var name = McpAgents().Select(a => a.Name).FirstOrDefault(a => a.Equals(agent.TrimStart('@'), StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"Agente \"{agent}\" inesistente o non raggiungibile via MCP (protocollo diverso da MCP).");
        var id = await ChannelAsync(channel);
        if (!(await store.ChannelAgentNamesAsync(id)).Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new McpException($"{name} non è un agente del canale \"{channel}\": chi ha creato il canale può aggiungerlo.");
        long after = 0; // diventa l'id della domanda appena scritta: le risposte arrivano sempre dopo
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(WorkspaceEvent e)
        {
            if (e is not { Kind: "message", Message: { } m } || m.ChannelId != id || m.Id <= Volatile.Read(ref after)) return;
            if (m.Kind == AuthorKind.Agent && m.Author.Equals(name, StringComparison.OrdinalIgnoreCase)) reply.TrySetResult(m.Text);
            else if (m.Kind == AuthorKind.System && m.Text.StartsWith($"{name} non ha potuto rispondere", StringComparison.OrdinalIgnoreCase))
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
            return $"{name} non ha ancora risposto: la risposta comparirà nel canale (usa read_messages).";
        }
        finally { bus.Published -= OnEvent; }
    });

    async Task<int> ChannelAsync(string channel)
    {
        if (int.TryParse(channel, out var id)) return id;
        var name = channel.TrimStart('#');
        return (await store.GetChannelsAsync(User)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new McpException($"Canale \"{channel}\" inesistente.");
    }

    // Errori del dominio come errori MCP leggibili dal modello (le altre eccezioni il SDK le nasconde).
    static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (KeyNotFoundException ex) { throw new McpException(ex.Message); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
    }
}
