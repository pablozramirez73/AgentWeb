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

    [McpServerTool(Name = "create_channel"), Description("Crea un canale (ne diventi il creatore: solo tu scegli i suoi agenti). Restituisce id e nome.")]
    public Task<ChannelDto> CreateChannel(
        [Description("Nome del canale (spazi diventano trattini)")] string name,
        [Description("true = privato, lo vedono solo i membri (default false)")] bool @private = false) =>
        Run(() => store.CreateChannelAsync(name, User, @private));

    [McpServerTool(Name = "add_agent_to_channel"), Description("Aggiunge un agente AgentWeb a un canale che hai creato: da lì risponde alle @menzioni e svolge i task.")]
    public Task<string> AddAgentToChannel([Description("Nome dell'agente")] string agent, [Description("Id o nome del canale")] string channel) => Run(async () =>
    {
        await store.AddChannelAgentAsync(await ChannelAsync(channel), User, agent);
        return $"{agent.TrimStart('@')} è un agente del canale {channel}.";
    });

    // Solo amministratori, e niente URL o chiavi via MCP: il nuovo agente usa l'LLM (server e token) di un agente già configurato.
    [McpServerTool(Name = "create_agent"), Description("Solo amministratori. Crea un agente AgentWeb che usa lo stesso LLM di un agente esistente (eventualmente con un altro modello).")]
    public Task<string> CreateAgent(
        [Description("Nome (lettere, numeri, _ e -): si menziona con @nome")] string name,
        [Description("Scopo dell'agente: diventa il suo system prompt")] string purpose,
        [Description("Agente esistente di cui usare l'LLM (default: il primo agente abilitato)")] string? like = null,
        [Description("Modello diverso da quello dell'agente copiato (facoltativo)")] string? model = null,
        [Description("'internal' (default), 'mcp' (chiamabile con ask_agent) oppure 'a2a'")] string protocol = AgentProtocols.Internal,
        [Description("Canale (id o nome) a cui aggiungerlo subito, se l'hai creato tu (facoltativo)")] string? channel = null) => Run(async () =>
    {
        RequireAdmin("creare agenti");
        var s = settings.Get();
        var source = (like is null ? s.LocalAgents.FirstOrDefault(a => a.Enabled) : s.FindLocal(like.TrimStart('@')))
            ?? throw new McpException(like is null ? "Nessun agente esistente da cui copiare l'LLM: creane uno dalla pagina Agenti." : $"Agente \"{like}\" inesistente o disabilitato.");
        if (AgentProtocols.All.All(x => x.Value != protocol)) throw new McpException("protocol deve essere 'internal', 'mcp' o 'a2a'.");
        var src = source.Llm;
        s.LocalAgents.Add(new()
        {
            Name = name.Trim().TrimStart('@'), Prompt = purpose.Trim(), Protocol = protocol, Tools = [.. AgentTools.Defaults],
            Llm = new() { Kind = src.Kind, Url = src.Url, ApiKey = src.ApiKey, DefaultModel = string.IsNullOrWhiteSpace(model) ? src.DefaultModel : model.Trim() },
        });
        SaveSettings(s);
        var created = s.LocalAgents[^1];
        if (channel is not null) await store.AddChannelAgentAsync(await ChannelAsync(channel), User, created.Name);
        return $"Agente {created.Name} creato (LLM di {source.Name}, modello {created.Llm.DefaultModel})" + (channel is null ? "." : $" e aggiunto al canale {channel}.");
    });

    [McpServerTool(Name = "add_member"), Description("Aggiunge una persona (email) a un canale privato che vedi.")]
    public Task<string> AddMember([Description("Id o nome del canale")] string channel, [Description("Email dell'utente AgentWeb")] string user) => Run(async () =>
    {
        await store.AddMemberAsync(await ChannelAsync(channel), User, user.Trim());
        return $"{user.Trim()} è membro del canale {channel}.";
    });

    [McpServerTool(Name = "remove_member"), Description("Toglie una persona da un canale privato (solo chi l'ha creato), oppure ti fa uscire se indichi te stesso.")]
    public Task<string> RemoveMember([Description("Id o nome del canale")] string channel, [Description("Email dell'utente")] string user) => Run(async () =>
    {
        await store.RemoveMemberAsync(await ChannelAsync(channel), User, user.Trim());
        return $"{user.Trim()} non è più membro del canale {channel}.";
    });

    void RequireAdmin(string what)
    {
        if (http.HttpContext?.User.IsInRole(AccountEndpoints.AdminRole) != true) throw new McpException($"Solo un amministratore può {what}.");
    }

    // Come create_agent: niente server o chiavi dell'LLM via MCP, solo ciò che si sceglie nella scheda dell'agente.
    [McpServerTool(Name = "update_agent"), Description("Solo amministratori. Modifica un agente AgentWeb: scopo, modello, protocollo, abilitato. I campi omessi restano com'erano.")]
    public Task<string> UpdateAgent(
        [Description("Nome dell'agente")] string name,
        [Description("Nuovo scopo (system prompt)")] string? purpose = null,
        [Description("Nuovo modello (dello stesso server LLM)")] string? model = null,
        [Description("'internal', 'mcp' oppure 'a2a'")] string? protocol = null,
        [Description("true = abilitato, false = disabilitato")] bool? enabled = null) => Run(async () =>
    {
        RequireAdmin("modificare gli agenti");
        var s = settings.Get();
        var a = s.LocalAgents.FirstOrDefault(x => x.Name.Equals(name.TrimStart('@'), StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"Agente \"{name}\" inesistente.");
        if (protocol is not null && AgentProtocols.All.All(x => x.Value != protocol)) throw new McpException("protocol deve essere 'internal', 'mcp' o 'a2a'.");
        if (purpose is not null) a.Prompt = purpose.Trim();
        if (!string.IsNullOrWhiteSpace(model)) a.Llm.DefaultModel = model.Trim();
        if (protocol is not null) a.Protocol = protocol;
        if (enabled is { } e) a.Enabled = e;
        SaveSettings(s);
        await Task.CompletedTask;
        return $"Agente {a.Name}: modello {a.Llm.DefaultModel}, protocollo {a.Protocol}, {(a.Enabled ? "abilitato" : "disabilitato")}.";
    });

    [McpServerTool(Name = "add_a2a_agent"), Description("Solo amministratori. Collega un agente esterno via A2A (Agent Card in <url>/.well-known/agent-card.json): poi si @menziona e si aggiunge ai canali come gli altri.")]
    public Task<string> AddA2AAgent(
        [Description("Nome con cui menzionarlo (lettere, numeri, _ e -)")] string name,
        [Description("URL base dell'agente A2A, raggiungibile dal server")] string url,
        [Description("Bearer token, se l'agente lo chiede (facoltativo)")] string? token = null) => Run(async () =>
    {
        RequireAdmin("collegare agenti A2A");
        var peer = new A2AAgentConfig { Name = name.Trim().TrimStart('@'), Url = url.Trim(), BearerToken = token?.Trim() ?? "" };
        string card;
        try { card = await settings.TestA2AAsync(peer).WaitAsync(TimeSpan.FromSeconds(30)); } // prima di salvare: un URL sbagliato si vede subito
        catch (Exception e) { throw new McpException($"Agent Card non raggiungibile su {peer.Url}: {e.GetBaseException().Message}"); }
        var s = settings.Get();
        s.A2AAgents.Add(peer);
        SaveSettings(s);
        return $"Agente A2A {peer.Name} collegato: {card}";
    });

    [McpServerTool(Name = "remove_a2a_agent"), Description("Solo amministratori. Scollega un agente A2A esterno.")]
    public Task<string> RemoveA2AAgent([Description("Nome dell'agente A2A")] string name) => Run(async () =>
    {
        RequireAdmin("scollegare agenti A2A");
        var s = settings.Get();
        if (s.A2AAgents.RemoveAll(a => a.Name.Equals(name.TrimStart('@'), StringComparison.OrdinalIgnoreCase)) == 0)
            throw new McpException($"Nessun agente A2A si chiama \"{name}\".");
        SaveSettings(s);
        await Task.CompletedTask;
        return $"Agente A2A {name.TrimStart('@')} scollegato.";
    });

    // shortcut: legge e riscrive le impostazioni intere; un salvataggio contemporaneo dalla pagina Agenti può sovrascriverlo.
    void SaveSettings(AgentSettings s)
    {
        if (s.Validate(settings.AllowStdio) is { Count: > 0 } errors) throw new McpException(string.Join(" ", errors));
        settings.Save(s);
    }

    // Documenti: la memoria del canale che gli agenti consultano (stessi permessi della pagina Memoria del canale).
    [McpServerTool(Name = "list_documents", ReadOnly = true), Description("Elenca i documenti caricati nella memoria di un canale (id, titolo, autore, parti).")]
    public Task<List<string>> ListDocuments([Description("Id o nome del canale")] string channel) => Run(async () =>
        (await store.GetChannelMemoryAsync(await ChannelAsync(channel), User)).Where(m => m.Kind == "document")
            .Select(d => $"{d.SourceId}: «{d.Title}» di {d.CreatedBy}, {d.CreatedAt:yyyy-MM-dd}, {d.Chunks} parti").ToList());

    [McpServerTool(Name = "upload_document"), Description("Carica un documento di testo (anche Markdown) nella memoria di un canale: gli agenti del canale lo useranno per rispondere.")]
    public Task<string> UploadDocument(
        [Description("Id o nome del canale")] string channel,
        [Description("Titolo o nome del file")] string title,
        [Description("Testo del documento (massimo circa 1 MB)")] string text) => Run(async () =>
    {
        var parts = await store.UploadDocumentAsync(await ChannelAsync(channel), User, title, text);
        return $"«{title}» caricato nella memoria del canale ({parts} parti).";
    });

    [McpServerTool(Name = "search_documents", ReadOnly = true), Description("Cerca per significato nei documenti e nelle note della memoria di un canale; restituisce i passaggi più pertinenti.")]
    public Task<List<string>> SearchDocuments(
        [Description("Id o nome del canale")] string channel,
        [Description("Cosa cerchi, in parole tue")] string query,
        [Description("Quanti passaggi al massimo (1-20, default 5)")] int limit = 5) => Run(async () =>
        (await store.SearchChannelMemoryAsync(await ChannelAsync(channel), User, query, Math.Clamp(limit, 1, 20)))
            .Select(h => $"[{h.Title ?? h.Kind}] {h.Content}").ToList());

    [McpServerTool(Name = "delete_document"), Description("Elimina un documento dalla memoria di un canale. Può farlo chi l'ha caricato o chi ha creato il canale.")]
    public Task<string> DeleteDocument([Description("Id o nome del canale")] string channel, [Description("Id del documento (vedi list_documents) o titolo esatto")] string document) => Run(async () =>
    {
        var id = await ChannelAsync(channel);
        var docs = (await store.GetChannelMemoryAsync(id, User)).Where(m => m.Kind == "document").ToList();
        var matches = docs.Where(d => d.SourceId == document).ToList() is { Count: > 0 } byId ? byId
            : docs.Where(d => string.Equals(d.Title, document, StringComparison.OrdinalIgnoreCase)).ToList();
        var doc = matches switch
        {
            [var one] => one,
            [] => throw new McpException($"Documento «{document}» inesistente in questo canale (vedi list_documents)."),
            _ => throw new McpException($"Più documenti si chiamano «{document}»: indica l'id (vedi list_documents)."),
        };
        await store.DeleteChannelMemoryAsync(id, User, doc.Id, doc.SourceId);
        return $"«{doc.Title}» eliminato.";
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
        catch (UnauthorizedAccessException ex) { throw new McpException(ex.Message); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }
}
