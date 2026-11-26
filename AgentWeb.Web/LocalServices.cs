using System.Security.Claims;
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Workspace;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentWeb.Web;

// Implementazioni "in processo" per le pagine Blazor renderizzate sul server (prerender e InteractiveServer).
// Gli eventi passano dallo stesso filtro dell'hub: canale osservato + ChannelVisibility.
public sealed class LocalWorkspaceClient(WorkspaceStore store, WorkspaceBus bus, AuthenticationStateProvider auth, AgentActivity activity) : IWorkspaceClient, IDisposable
{
    bool started;
    int? watched;
    string? user;

    public event Action<WorkspaceEvent>? Changed;

    public async Task StartAsync()
    {
        user ??= await UserAsync();
        if (!started) { bus.Published += Forward; started = true; }
    }

    void Forward(WorkspaceEvent e) => _ = ForwardAsync(e);

    async Task ForwardAsync(WorkspaceEvent e)
    {
        if (user is null) return;
        if (e.Kind is "channel" or "message" ? await store.CanAccessAsync(e.ChannelId, user) : e.ChannelId == watched)
            Changed?.Invoke(e);
    }

    public async Task<ReplayDto?> WatchChannelAsync(int? channelId, long afterId = -1)
    {
        watched = null;
        if (channelId is not { } id) return null;
        var user = await UserAsync();
        if (!await store.CanAccessAsync(id, user)) throw new KeyNotFoundException($"Canale {id} inesistente.");
        watched = id;
        return afterId >= 0 ? await store.GetMessagesSinceAsync(id, user, afterId) : null;
    }

    public async Task SetTypingAsync(int channelId, bool typing)
    {
        var user = await UserAsync();
        if (await store.CanAccessAsync(channelId, user)) bus.Publish(new("typing", channelId, Agent: user, Typing: typing, Human: true));
    }

    public async Task<ChannelAgentsDto> GetChannelAgentsAsync(int channelId) => await store.GetChannelAgentsAsync(channelId, await UserAsync());
    public async Task AddChannelAgentAsync(int channelId, string agent) => await store.AddChannelAgentAsync(channelId, await UserAsync(), agent);
    public async Task RemoveChannelAgentAsync(int channelId, string agent) => await store.RemoveChannelAgentAsync(channelId, await UserAsync(), agent);
    public async Task<List<MemoryDto>> GetChannelMemoryAsync(int channelId) => await store.GetChannelMemoryAsync(channelId, await UserAsync());
    public async Task UploadDocumentAsync(int channelId, string fileName, string text) => await store.UploadDocumentAsync(channelId, await UserAsync(), fileName, text);
    public async Task DeleteMemoryAsync(int channelId, long memoryId) => await store.DeleteChannelMemoryAsync(channelId, await UserAsync(), memoryId, null);
    public async Task DeleteDocumentAsync(int channelId, string sourceId) => await store.DeleteChannelMemoryAsync(channelId, await UserAsync(), 0, sourceId);

    async Task<string> UserAsync() =>
        (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? throw new UnauthorizedAccessException("Non autenticato.");

    public async Task<List<ChannelDto>> GetChannelsAsync() => await store.GetChannelsAsync(await UserAsync());
    public async Task<ChannelDto> CreateChannelAsync(string name, bool isPrivate = false) => await store.CreateChannelAsync(name, await UserAsync(), isPrivate);
    public async Task<List<string>> GetMembersAsync(int channelId) => await store.GetMembersAsync(channelId, await UserAsync());
    public async Task AddMemberAsync(int channelId, string userName) => await store.AddMemberAsync(channelId, await UserAsync(), userName);
    public async Task<List<MessageDto>> GetMessagesAsync(int channelId) => await store.GetMessagesAsync(channelId, await UserAsync());
    public async Task<ReplayDto> GetMessagesSinceAsync(int channelId, long afterId) => await store.GetMessagesSinceAsync(channelId, await UserAsync(), afterId);
    public async Task SendMessageAsync(int channelId, string text, long? parentId = null) => await store.PostUserMessageAsync(channelId, await UserAsync(), text, parentId: parentId);
    public async Task MarkReadAsync(int channelId, long lastId) => await store.MarkReadAsync(channelId, await UserAsync(), lastId);
    public async Task<List<SearchHitDto>> SearchAsync(string query) => await store.SearchAsync(await UserAsync(), query);
    public Task<string> WhoAmIAsync() => UserAsync();
    public async Task<ActivityDto> GetActivityAsync() => await store.GetActivityAsync(await UserAsync(), activity);
    public async Task<StatsDto> GetStatsAsync(int days) => await store.GetStatsAsync(await UserAsync(), days);
    public async Task<AgentLogDto> GetAgentLogAsync(string agent, int days) => await store.GetAgentLogAsync(await UserAsync(), agent, days);
    public async Task<List<FeedItemDto>> GetFeedAsync() => await store.GetFeedAsync(await UserAsync());
    public async Task<List<MessageDto>> GetThreadAsync(int channelId, long rootId) => await store.GetThreadAsync(channelId, await UserAsync(), rootId);
    public async Task MarkThreadReadAsync(int channelId, long rootId, long lastId) => await store.MarkThreadReadAsync(channelId, await UserAsync(), rootId, lastId);
    public async Task<ChannelNotifyDto> GetChannelNotifyAsync(int channelId) => await store.GetChannelNotifyAsync(channelId, await UserAsync());
    public async Task SetChannelNotifyAsync(int channelId, string level) => await store.SetChannelNotifyAsync(channelId, await UserAsync(), level);
    public async Task<ChannelDto> OpenDirectAsync(string with) => await store.OpenDirectAsync(await UserAsync(), with);
    public async Task<List<string>> GetPeopleAsync() => await store.GetPeopleAsync(await UserAsync());
    public async Task<List<TaskDto>> GetAllTasksAsync() => await store.GetAllTasksAsync(await UserAsync());
    public async Task<List<TaskDto>> GetTasksAsync(int channelId) => await store.GetTasksAsync(channelId, await UserAsync());
    public async Task<TaskDto> CreateTaskFromMessageAsync(int channelId, long messageId) => await store.CreateTaskFromMessageAsync(channelId, messageId, await UserAsync());
    public async Task<TaskDto> SendAsTaskAsync(int channelId, string text) => await store.SendAsTaskAsync(channelId, await UserAsync(), text);
    public async Task<TaskDto> CreateTaskAsync(int channelId, NewTaskRequest task) => await store.CreateTaskAsync(channelId, task, await UserAsync());
    public async Task SetTaskStatusAsync(int taskId, WorkStatus status) => await store.SetStatusByUserAsync(taskId, status, await UserAsync());
    public Task<List<string>> GetAgentsAsync() => store.GetAgentsAsync();
    public Task<Dictionary<string, string>> GetAgentIconsAsync() => store.GetAgentIconsAsync();

    public void Dispose() => bus.Published -= Forward;
}

public sealed class LocalUserSession(AuthenticationStateProvider auth, NavigationManager nav) : IUserSession
{
    public event Action? Changed { add { } remove { } }
    public bool NeedsLogin => false; // sul web il cookie è obbligatorio: senza login il server reindirizza a /login
    public string? UserName { get; private set; }

    public async Task InitializeAsync() => UserName = (await auth.GetAuthenticationStateAsync()).User.Identity?.Name;
    public Task<string?> LoginAsync(string email, string password, string? code = null) => Task.FromResult<string?>("Usa la pagina /login.");
    public Task<string?> RegisterAsync(string email, string password) => Task.FromResult<string?>("Usa la pagina /login.");
    public Task<bool> CanRegisterAsync() => Task.FromResult(false); // sul web lo decide la pagina /login
    public Task LogoutAsync() { nav.NavigateTo("logout", forceLoad: true); return Task.CompletedTask; }
}

// Sessioni dell'utente corrente (prerender della pagina Dispositivi): stesse regole di /api/account/sessions.
public sealed class LocalAccountClient(SessionStore sessions, AuthenticationStateProvider auth, NavigationManager nav, UserManager<AppUser> users,
    Mailer mailer, Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider schemes, IDbContextFactory<AppDb> dbf, PushSender push) : IAccountClient
{
    public async Task<NotificationSettingsDto> GetNotificationSettingsAsync() { await using var db = await dbf.CreateDbContextAsync(); return await PushEndpoints.GetSettingsAsync(db, (await CurrentAsync()).User); }
    public async Task SaveNotificationSettingsAsync(NotificationSettingsDto s) { await using var db = await dbf.CreateDbContextAsync(); await PushEndpoints.SaveSettingsAsync(db, (await CurrentAsync()).User, s); }
    public Task<PushConfigDto> GetPushConfigAsync() => Task.FromResult(new PushConfigDto(push.VapidPublicKey, push.Kinds));
    public async Task RegisterPushDeviceAsync(PushDeviceRequest d) { var (u, sid) = await CurrentAsync(); await using var db = await dbf.CreateDbContextAsync(); await PushEndpoints.RegisterAsync(db, push, u, sid, d); }
    public async Task UnregisterPushDeviceAsync(string token) { await using var db = await dbf.CreateDbContextAsync(); await PushEndpoints.UnregisterAsync(db, (await CurrentAsync()).User, token); }
    public async Task<int> SendTestPushAsync() => await push.SendAsync((await CurrentAsync()).User, new("AgentWeb", "Le notifiche funzionano su questo dispositivo.", "notifications"));

    async Task<AppUser> UserAsync() => await users.FindByNameAsync((await CurrentAsync()).User) ?? throw new UnauthorizedAccessException();

    public async Task<ProfileDto> GetProfileAsync() => await Profile.GetAsync(users, mailer, schemes, await UserAsync());
    public async Task SaveProfileAsync(string? displayName) => await Profile.SaveAsync(users, await UserAsync(), displayName);
    public async Task SetAvatarAsync(string? contentType, byte[]? data) => await Profile.SetAvatarAsync(users, await UserAsync(), contentType, data);
    public async Task SendConfirmationEmailAsync() => await Profile.SendConfirmationAsync(users, mailer, await UserAsync(), new Uri(nav.BaseUri));
    public async Task RemoveLoginAsync(string provider) => await Profile.RemoveLoginAsync(users, await UserAsync(), provider);

    public Uri ServerBase => new(nav.BaseUri);

    async Task<(string User, string? Sid)> CurrentAsync()
    {
        var u = (await auth.GetAuthenticationStateAsync()).User;
        return (u.Identity?.Name ?? throw new UnauthorizedAccessException("Non autenticato."), u.FindFirstValue(SessionStore.Claim));
    }

    public async Task<List<SessionDto>> GetSessionsAsync() { var (u, sid) = await CurrentAsync(); return await sessions.ListAsync(u, sid); }
    public async Task RevokeSessionAsync(string id) { var (u, _) = await CurrentAsync(); await sessions.RevokeAsync(id, u); }
    public async Task<int> RevokeAllSessionsAsync() { var (u, _) = await CurrentAsync(); return await sessions.RevokeAllAsync(u); }
    public async Task ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var (u, sid) = await CurrentAsync();
        var user = await users.FindByNameAsync(u) ?? throw new UnauthorizedAccessException();
        var r = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!r.Succeeded) throw new InvalidOperationException(string.Join(" ", r.Errors.Select(e => e.Description)));
        await sessions.RevokeAllAsync(u, except: sid);
    }
    public async Task<ApiTokenDto> CreateTokenAsync(string name) { var (u, _) = await CurrentAsync(); return await sessions.CreateTokenAsync(u, name); }

    // Il prerender della pagina Dispositivi legge solo lo stato; attivare/disattivare passa dall'API (rate limit compreso).
    public async Task<TwoFactorDto> GetTwoFactorAsync()
    {
        var (u, _) = await CurrentAsync();
        return await TwoFactor.StatusAsync(users, await users.FindByNameAsync(u) ?? throw new UnauthorizedAccessException());
    }
    public Task EnableTwoFactorAsync(string code) => throw new InvalidOperationException("Disponibile solo dal browser o dall'app.");
    public Task DisableTwoFactorAsync(string code) => throw new InvalidOperationException("Disponibile solo dal browser o dall'app.");
}

// Le impostazioni contengono token e possono avviare comandi (stdio): solo amministratori.
public sealed class LocalAgentSettingsService(AgentSettingsStore store, AuthenticationStateProvider auth) : IAgentSettingsService
{
    async Task RequireAdminAsync()
    {
        if (!(await auth.GetAuthenticationStateAsync()).User.IsInRole(AccountEndpoints.AdminRole))
            throw new UnauthorizedAccessException("Solo gli amministratori possono vedere e modificare gli agenti.");
    }

    public async Task<AgentSettingsView> GetAsync(CancellationToken ct = default) { await RequireAdminAsync(); return await store.GetAsync(ct); }
    public async Task SaveAsync(AgentSettings settings, CancellationToken ct = default) { await RequireAdminAsync(); await store.SaveAsync(settings, ct); }
    public async Task<string> TestMcpAsync(McpServerConfig server, CancellationToken ct = default) { await RequireAdminAsync(); return await store.TestMcpAsync(server, ct); }
    public async Task<string> TestA2AAsync(A2AAgentConfig agent, CancellationToken ct = default) { await RequireAdminAsync(); return await store.TestA2AAsync(agent, ct); }
    public async Task<string> TestLlmAsync(LlmProviderConfig provider, CancellationToken ct = default) { await RequireAdminAsync(); return await store.TestLlmAsync(provider, ct); }
    public async Task<List<string>> ListModelsAsync(LlmProviderConfig provider, CancellationToken ct = default) { await RequireAdminAsync(); return await store.ListModelsAsync(provider, ct); }
}

// Hub del workspace (browser WebAssembly e app MAUI). Gruppi:
//   "u:{utente}"  -> eventi "channel" dei canali privati di cui è membro
//   "ch:{id}"     -> task/typing/agenti del canale, solo dopo Watch con ChannelVisibility
// I messaggi vanno a tutti quelli che vedono il canale (anche senza guardarlo: badge dei non letti), una volta sola.
[Authorize]
public sealed class WorkspaceHub(WorkspaceStore store, SessionConnections connections) : Hub
{
    public static string UserGroup(string user) => "u:" + user;
    public static string ChannelGroup(int id) => "ch:" + id;

    string User => Context.User!.Identity!.Name!;
    string? Sid => Context.User!.FindFirstValue(SessionStore.Claim);

    public override async Task OnConnectedAsync()
    {
        if (Sid is { } sid) connections.Add(sid, Context);
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(User));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Sid is { } sid) connections.Remove(sid, Context.ConnectionId);
        return Task.CompletedTask;
    }

    // Iscrizione + recupero in una chiamata: afterId >= 0 restituisce i messaggi successivi (stessa regola di /messages/since).
    public async Task<HubWatchReply> WatchFrom(int? channelId, long afterId)
    {
        if (!await Watch(channelId)) return new(NotFound: true, null);
        return new(false, channelId is { } id && afterId >= 0 ? await store.GetMessagesSinceAsync(id, User, afterId) : null);
    }

    // Invio sulla connessione aperta: stessa autorizzazione e stessa idempotenza (ClientId) della POST HTTP.
    public Task<HubSendReply> SendMessage(int channelId, string text, string? clientId) => Reply(channelId, null, text, clientId);

    // Come SendMessage, dentro il thread di parentId (metodo separato: i client già installati mandano 3 argomenti).
    public async Task<HubSendReply> Reply(int channelId, long? parentId, string text, string? clientId)
    {
        try { return new((await store.PostUserMessageAsync(channelId, User, text, clientId, parentId)).Id); }
        catch (KeyNotFoundException) { return new(null, NotFound: true); }
        catch (ArgumentException ex) { return new(null, Error: ex.Message); }
    }

    // "Sta scrivendo": solo agli altri che guardano il canale, e solo se chi scrive lo vede.
    public async Task Typing(int channelId, bool typing)
    {
        if (!await store.CanAccessAsync(channelId, User)) return;
        await Clients.OthersInGroup(ChannelGroup(channelId)).SendAsync("event", new WorkspaceEvent("typing", channelId, Agent: User, Typing: typing, Human: true));
    }

    // Un canale osservato per connessione: cambiare canale esce dal gruppo precedente.
    public async Task<bool> Watch(int? channelId)
    {
        if (Context.Items.TryGetValue("watched", out var prev) && prev is int old)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChannelGroup(old));
            Context.Items.Remove("watched");
        }
        if (channelId is not { } id) return true;
        if (!await store.CanAccessAsync(id, User)) return false;
        await Groups.AddToGroupAsync(Context.ConnectionId, ChannelGroup(id));
        Context.Items["watched"] = id;
        return true;
    }
}

// Inoltra gli eventi del workspace ai client SignalR, rispettando la visibilità (con Redis anche alle altre repliche).
// Una coda per gruppo di canali (hash dell'id): l'ordine resta per canale anche se l'instradamento interroga il database,
// e un canale lento non ferma gli altri. Code limitate e invii con timeout: un DB o un backplane bloccato non riempie la memoria.
public sealed class WorkspaceHubBridge(WorkspaceBus bus, IHubContext<WorkspaceHub> hub, WorkspaceStore store, ILogger<WorkspaceHubBridge> log) : BackgroundService
{
    const int Lanes = 8;
    static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    readonly System.Threading.Channels.Channel<WorkspaceEvent>[] lanes = [.. Enumerable.Range(0, Lanes).Select(_ =>
        System.Threading.Channels.Channel.CreateBounded<WorkspaceEvent>(new System.Threading.Channels.BoundedChannelOptions(10_000) { SingleReader = true }))];

    // Coda piena: l'evento si perde (i messaggi restano nel DB e i client li recuperano con WatchFrom), ma lo si dice.
    void Enqueue(WorkspaceEvent e)
    {
        if (!lanes[(uint)e.ChannelId % Lanes].Writer.TryWrite(e)) log.LogWarning("Coda realtime piena: evento {Kind} del canale {Channel} scartato", e.Kind, e.ChannelId);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        bus.Published += Enqueue;
        try { await Task.WhenAll(lanes.Select(l => DrainAsync(l.Reader, ct))); }
        catch (OperationCanceledException) { }
        finally { bus.Published -= Enqueue; }
    }

    async Task DrainAsync(System.Threading.Channels.ChannelReader<WorkspaceEvent> reader, CancellationToken ct)
    {
        await foreach (var e in reader.ReadAllAsync(ct))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SendTimeout);
            try { await SendAsync(e, timeout.Token); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning(ex, "Evento {Kind} non inoltrato", e.Kind); }
        }
    }

    async Task SendAsync(WorkspaceEvent e, CancellationToken ct)
    {
        if (e.Kind is not ("channel" or "message"))
        {
            await hub.Clients.Group(WorkspaceHub.ChannelGroup(e.ChannelId)).SendAsync("event", e, ct);
            return;
        }
        // Solo chi può vedere il canale: i membri se privato, tutti gli autenticati se pubblico.
        var to = await store.PrivateMembersAsync(e.ChannelId) is { } members
            ? hub.Clients.Groups(members.Select(WorkspaceHub.UserGroup).ToList())
            : hub.Clients.All;
        await to.SendAsync("event", e, ct);
        // shortcut: copia "activity" per le app MAUI già installate, che leggono i badge dei non letti solo da lì
        // (i client nuovi la ignorano). Da togliere quando quelle versioni non sono più in uso.
        if (e.Kind == "message") await to.SendAsync("event", e with { Kind = "activity" }, ct);
    }
}
