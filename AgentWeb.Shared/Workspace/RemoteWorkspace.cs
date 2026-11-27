using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentWeb.Shared.Agents;
using Microsoft.AspNetCore.SignalR.Client;

namespace AgentWeb.Shared.Workspace;

// Dove l'app salva i token (SecureStorage su MAUI, memoria nei test).
public interface ITokenStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string? value); // null = cancella
}

// Login con gli endpoint Identity del server (/identity/login, /identity/refresh): token bearer + refresh token.
public sealed class RemoteUserSession(HttpClient anonymous, ITokenStore store) : IUserSession
{
    sealed record TokenResponse(string AccessToken, string RefreshToken, long ExpiresIn);

    readonly SemaphoreSlim refreshGate = new(1, 1);
    string? access, refresh;
    DateTimeOffset expiresAt;

    public event Action? Changed;
    public bool NeedsLogin => refresh is null;
    public string? UserName { get; private set; }

    public async Task InitializeAsync()
    {
        refresh = await store.GetAsync("refresh");
        UserName = await store.GetAsync("user");
        // App: la lingua la decide il server; senza rete resta quella dell'ultima volta.
        try { Lang.Use(await anonymous.GetStringAsync("language")); await store.SetAsync("lang", Lang.Code); }
        catch (HttpRequestException) { Lang.Use(await store.GetAsync("lang")); }
        catch (TaskCanceledException) { Lang.Use(await store.GetAsync("lang")); }
    }

    public async Task<string?> LoginAsync(string email, string password, string? code = null)
    {
        using var r = await anonymous.PostAsJsonAsync("identity/login", new { email, password, twoFactorCode = code });
        if (r.StatusCode == HttpStatusCode.Unauthorized && (await r.Content.ReadAsStringAsync()).Contains("RequiresTwoFactor"))
            return Lang.T(string.IsNullOrWhiteSpace(code) ? "auth.enterCode" : "auth.badCode");
        if (!r.IsSuccessStatusCode) return r.StatusCode == HttpStatusCode.Unauthorized ? Lang.T("auth.badCredentials") : $"Server: HTTP {(int)r.StatusCode}";
        await SaveAsync((await r.Content.ReadFromJsonAsync<TokenResponse>())!, email);
        return null;
    }

    public async Task<bool> CanRegisterAsync()
    {
        try { return await anonymous.GetFromJsonAsync<bool>("account/registration-open"); }
        catch (HttpRequestException) { return false; } // server irraggiungibile: il login mostrerà l'errore
    }

    public async Task<string?> RegisterAsync(string email, string password)
    {
        using var r = await anonymous.PostAsJsonAsync("account/register", new { email, password });
        if (!r.IsSuccessStatusCode) return await r.Content.ReadAsStringAsync();
        return await LoginAsync(email, password);
    }

    public async Task LogoutAsync()
    {
        // Prima revoca la sessione sul server (così il refresh token rubato o copiato non vale più), poi pulisce il telefono.
        // Best effort: se il token è già scaduto o la rete manca, la sessione resta finché non la revoca "Dispositivi".
        if (access is { } token && DateTimeOffset.UtcNow < expiresAt)
        {
            try
            {
                using var me = new HttpRequestMessage(HttpMethod.Get, "api/account/me");
                me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var r = await anonymous.SendAsync(me);
                if (r.IsSuccessStatusCode && (await r.Content.ReadFromJsonAsync<AccountInfo>())?.SessionId is { } sid)
                {
                    using var revoke = new HttpRequestMessage(HttpMethod.Post, $"api/account/sessions/{sid}/revoke");
                    revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    (await anonymous.SendAsync(revoke)).Dispose();
                }
            }
            catch (HttpRequestException) { }
        }
        access = refresh = UserName = null;
        await store.SetAsync("refresh", null);
        await store.SetAsync("user", null);
        Changed?.Invoke();
    }

    // Usato da ogni chiamata API e da SignalR: rinnova l'access token (1 h) col refresh token quando scade.
    public async Task<string?> GetAccessTokenAsync()
    {
        await refreshGate.WaitAsync();
        try
        {
            if (access != null && DateTimeOffset.UtcNow < expiresAt) return access;
            if (refresh is null) return null;
            using var r = await anonymous.PostAsJsonAsync("identity/refresh", new { refreshToken = refresh });
            if (!r.IsSuccessStatusCode) { _ = LogoutAsync(); return null; }
            await SaveAsync((await r.Content.ReadFromJsonAsync<TokenResponse>())!, UserName);
            return access;
        }
        finally { refreshGate.Release(); }
    }

    async Task SaveAsync(TokenResponse t, string? user)
    {
        access = t.AccessToken;
        refresh = t.RefreshToken;
        expiresAt = DateTimeOffset.UtcNow.AddSeconds(t.ExpiresIn - 60);
        UserName = user;
        await store.SetAsync("refresh", refresh);
        await store.SetAsync("user", user);
        Changed?.Invoke();
    }
}

// Aggiunge il bearer token a ogni richiesta API; un 401 vuol dire sessione scaduta -> torna al login.
public sealed class BearerHandler(RemoteUserSession session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (await session.GetAccessTokenAsync() is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await session.LogoutAsync();
        return response;
    }
}

// Sessioni dell'utente via API (browser WebAssembly e app MAUI).
public sealed class RemoteAccountClient(HttpClient http) : IAccountClient
{
    public Uri ServerBase => http.BaseAddress!;

    public async Task<ProfileDto> GetProfileAsync()
    {
        using var r = await http.GetAsync("api/account/profile");
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
        return (await r.Content.ReadFromJsonAsync<ProfileDto>())!;
    }

    public async Task SaveProfileAsync(string? displayName)
    {
        using var r = await http.PutAsJsonAsync("api/account/profile", new ProfileRequest(displayName));
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }

    public async Task SetAvatarAsync(string? contentType, byte[]? data)
    {
        using var r = data is null ? await http.DeleteAsync("api/account/avatar")
            : await http.PutAsJsonAsync("api/account/avatar", new AvatarRequest(contentType ?? "", Convert.ToBase64String(data)));
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }

    public async Task SendConfirmationEmailAsync()
    {
        using var r = await http.PostAsync("api/account/email/confirm", null);
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }

    public async Task RemoveLoginAsync(string provider)
    {
        using var r = await http.DeleteAsync($"api/account/logins/{Uri.EscapeDataString(provider)}");
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }

    public async Task<NotificationSettingsDto> GetNotificationSettingsAsync() =>
        (await http.GetFromJsonAsync<NotificationSettingsDto>("api/account/notifications"))!;
    public Task SaveNotificationSettingsAsync(NotificationSettingsDto settings) => SendAsync(HttpMethod.Put, "api/account/notifications", settings);
    public async Task<PushConfigDto> GetPushConfigAsync() => (await http.GetFromJsonAsync<PushConfigDto>("api/push/config"))!;
    public Task RegisterPushDeviceAsync(PushDeviceRequest device) => SendAsync(HttpMethod.Post, "api/push/devices", device);
    public Task UnregisterPushDeviceAsync(string token) => SendAsync(HttpMethod.Post, "api/push/devices/remove", new PushTokenRequest(token));
    public async Task<int> SendTestPushAsync()
    {
        using var r = await http.PostAsync("api/push/test", null);
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
        return await r.Content.ReadFromJsonAsync<int>();
    }

    async Task SendAsync(HttpMethod method, string url, object body)
    {
        using var r = await http.SendAsync(new(method, url) { Content = JsonContent.Create(body) });
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }

    public async Task<List<SessionDto>> GetSessionsAsync()
    {
        using var r = await http.GetAsync("api/account/sessions");
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
        return (await r.Content.ReadFromJsonAsync<List<SessionDto>>())!;
    }

    public async Task RevokeSessionAsync(string id)
    {
        using var r = await http.PostAsync($"api/account/sessions/{Uri.EscapeDataString(id)}/revoke", null);
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }

    public async Task<int> RevokeAllSessionsAsync()
    {
        using var r = await http.PostAsync("api/account/sessions/revoke-all", null);
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
        return await r.Content.ReadFromJsonAsync<int>();
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword)
    {
        using var r = await http.PostAsJsonAsync("api/account/password", new ChangePasswordRequest(currentPassword, newPassword));
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }

    public async Task<TwoFactorDto> GetTwoFactorAsync()
    {
        using var r = await http.GetAsync("api/account/2fa");
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
        return (await r.Content.ReadFromJsonAsync<TwoFactorDto>())!;
    }

    public async Task EnableTwoFactorAsync(string code)
    {
        using var r = await http.PostAsJsonAsync("api/account/2fa/enable", new TwoFactorCodeRequest(code));
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }

    public async Task DisableTwoFactorAsync(string code)
    {
        using var r = await http.PostAsJsonAsync("api/account/2fa/disable", new TwoFactorCodeRequest(code));
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }

    public async Task<ApiTokenDto> CreateTokenAsync(string name)
    {
        using var r = await http.PostAsJsonAsync("api/account/tokens", new NewApiTokenRequest(name));
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
        return (await r.Content.ReadFromJsonAsync<ApiTokenDto>())!;
    }
}

// accessToken: MAUI passa il bearer token della sessione; nel browser (WebAssembly) è null e
// HTTP + SignalR usano il cookie di login, che sulla stessa origine viaggia da solo.
// configure: opzioni in più del client SignalR (es. MessagePack nell'app nativa, che lo aggiunge senza pesare sul browser).
public sealed class RemoteWorkspaceClient(HttpClient http, Func<Task<string?>>? accessToken = null, Action<IHubConnectionBuilder>? configure = null) : IWorkspaceClient, IAsyncDisposable
{
    HubConnection? hub;
    int? watched;

    public event Action<WorkspaceEvent>? Changed;

    public async Task StartAsync()
    {
        if (hub != null) return;
        var builder = new HubConnectionBuilder()
            .WithUrl(new Uri(http.BaseAddress!, "hubs/workspace"), o => { if (accessToken != null) o.AccessTokenProvider = accessToken; })
            .WithStatefulReconnect()   // brevi interruzioni: il server ritrasmette ciò che è rimasto nel buffer
            .WithAutomaticReconnect(); // interruzioni lunghe: nuova connessione, Watch di nuovo e replay ("resync")
        configure?.Invoke(builder);
        hub = builder.Build();
        hub.On<WorkspaceEvent>("event", e => Changed?.Invoke(e));
        // Nuova connessione = nuovi gruppi: si rientra nel canale osservato e la pagina recupera quanto perso (replay).
        hub.Reconnected += async _ =>
        {
            if (watched is { } id && await hub.InvokeAsync<bool>("Watch", id)) Changed?.Invoke(new("resync", id));
        };
        await hub.StartAsync();
    }

    bool Connected => hub?.State == HubConnectionState.Connected;

    // Iscrizione e recupero in un'unica chiamata sulla connessione già aperta; senza connessione, HTTP.
    public async Task<ReplayDto?> WatchChannelAsync(int? channelId, long afterId = -1)
    {
        watched = channelId;
        if (Connected)
        {
            var r = await hub!.InvokeAsync<HubWatchReply>("WatchFrom", channelId, afterId);
            if (r.NotFound) throw new KeyNotFoundException(Lang.T("nf.channel", channelId));
            return r.Replay;
        }
        return channelId is { } id && afterId >= 0 ? await GetMessagesSinceAsync(id, afterId) : null;
    }

    // "Sta scrivendo": solo sull'hub (su HTTP costerebbe troppo); se non c'è connessione semplicemente non parte.
    public Task SetTypingAsync(int channelId, bool typing) =>
        Connected ? hub!.SendAsync("Typing", channelId, typing) : Task.CompletedTask;

    public Task<List<MemoryDto>> GetChannelMemoryAsync(int channelId) => GetAsync<List<MemoryDto>>($"api/workspace/channels/{channelId}/memory");
    public Task UploadDocumentAsync(int channelId, string fileName, string text) =>
        PostAsync<object?>($"api/workspace/channels/{channelId}/documents", new { fileName, text });
    public async Task DeleteMemoryAsync(int channelId, long memoryId)
    {
        using var r = await http.DeleteAsync($"api/workspace/channels/{channelId}/memory/{memoryId}");
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }
    public async Task DeleteDocumentAsync(int channelId, string sourceId)
    {
        using var r = await http.DeleteAsync($"api/workspace/channels/{channelId}/documents/{Uri.EscapeDataString(sourceId)}");
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }

    public Task<ChannelAgentsDto> GetChannelAgentsAsync(int channelId) => GetAsync<ChannelAgentsDto>($"api/workspace/channels/{channelId}/agents");
    public Task AddChannelAgentAsync(int channelId, string agent) => PostAsync<object?>($"api/workspace/channels/{channelId}/agents", new { name = agent });
    public async Task RemoveChannelAgentAsync(int channelId, string agent)
    {
        using var r = await http.DeleteAsync($"api/workspace/channels/{channelId}/agents/{Uri.EscapeDataString(agent)}");
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }

    public Task<List<ChannelDto>> GetChannelsAsync() => GetAsync<List<ChannelDto>>("api/workspace/channels");
    public Task<ChannelDto> CreateChannelAsync(string name, bool isPrivate = false) =>
        PostAsync<ChannelDto>("api/workspace/channels", new NewChannelRequest(name, isPrivate));
    public Task<List<string>> GetMembersAsync(int channelId) => GetAsync<List<string>>($"api/workspace/channels/{channelId}/members");
    public Task AddMemberAsync(int channelId, string userName) => PostAsync<object?>($"api/workspace/channels/{channelId}/members", new { name = userName });
    public Task<List<MessageDto>> GetMessagesAsync(int channelId) => GetAsync<List<MessageDto>>($"api/workspace/channels/{channelId}/messages");
    public Task<ReplayDto> GetMessagesSinceAsync(int channelId, long afterId) =>
        GetAsync<ReplayDto>($"api/workspace/channels/{channelId}/messages/since/{afterId}");
    // Invio sulla connessione WebSocket (un frame, niente intestazioni HTTP né cookie); senza connessione, HTTP.
    // ClientId nuovo per ogni invio: se l'invio viene ripetuto (retry) il server non duplica il messaggio.
    public async Task SendMessageAsync(int channelId, string text, long? parentId = null)
    {
        var clientId = Guid.NewGuid().ToString("N");
        if (Connected)
        {
            var r = await hub!.InvokeAsync<HubSendReply>("Reply", channelId, parentId, text, clientId);
            if (r.NotFound) throw new KeyNotFoundException(Lang.T("nf.channel", channelId));
            if (r.Error is { } e) throw new InvalidOperationException(e);
            return;
        }
        await PostAsync<object?>($"api/workspace/channels/{channelId}/messages", new NewMessageRequest(text, clientId, parentId));
    }
    public Task MarkReadAsync(int channelId, long lastId) => PostAsync<object?>($"api/workspace/channels/{channelId}/read", new MarkReadRequest(lastId));
    public async Task<string> WhoAmIAsync() => (await GetAsync<AccountInfo>("api/account/me")).UserName;
    public Task<ActivityDto> GetActivityAsync() => GetAsync<ActivityDto>("api/workspace/activity");
    public Task<StatsDto> GetStatsAsync(int days) => GetAsync<StatsDto>($"api/workspace/stats?days={days}");
    public Task<AgentLogDto> GetAgentLogAsync(string agent, int days) => GetAsync<AgentLogDto>($"api/workspace/agents/{Uri.EscapeDataString(agent)}/log?days={days}");
    public Task<List<FeedItemDto>> GetFeedAsync() => GetAsync<List<FeedItemDto>>("api/workspace/feed");
    public Task<List<MessageDto>> GetThreadAsync(int channelId, long rootId) => GetAsync<List<MessageDto>>($"api/workspace/channels/{channelId}/messages/{rootId}/thread");
    public Task MarkThreadReadAsync(int channelId, long rootId, long lastId) =>
        PostAsync<object?>($"api/workspace/channels/{channelId}/threads/{rootId}/read", new MarkReadRequest(lastId));
    public Task<List<SearchHitDto>> SearchAsync(string query) => GetAsync<List<SearchHitDto>>($"api/workspace/search?q={Uri.EscapeDataString(query)}");
    public Task<ChannelNotifyDto> GetChannelNotifyAsync(int channelId) => GetAsync<ChannelNotifyDto>($"api/workspace/channels/{channelId}/notifications");
    public async Task SetChannelNotifyAsync(int channelId, string level)
    {
        using var r = await http.PutAsJsonAsync($"api/workspace/channels/{channelId}/notifications", new ChannelNotifyDto(level));
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
    }
    public Task<ChannelDto> OpenDirectAsync(string with) => PostAsync<ChannelDto>("api/workspace/direct", new { name = with });
    public Task<List<string>> GetPeopleAsync() => GetAsync<List<string>>("api/workspace/people");
    public Task<List<TaskDto>> GetAllTasksAsync() => GetAsync<List<TaskDto>>("api/workspace/tasks");
    public Task<List<TaskDto>> GetTasksAsync(int channelId) => GetAsync<List<TaskDto>>($"api/workspace/channels/{channelId}/tasks");
    public Task<TaskDto> CreateTaskFromMessageAsync(int channelId, long messageId) =>
        PostAsync<TaskDto>($"api/workspace/channels/{channelId}/messages/{messageId}/task", new { });
    public Task<TaskDto> SendAsTaskAsync(int channelId, string text) =>
        PostAsync<TaskDto>($"api/workspace/channels/{channelId}/tasks/from-text", new { text });
    public Task<TaskDto> CreateTaskAsync(int channelId, NewTaskRequest task) => PostAsync<TaskDto>($"api/workspace/channels/{channelId}/tasks", task);
    public Task SetTaskStatusAsync(int taskId, WorkStatus status) => PostAsync<object?>($"api/workspace/tasks/{taskId}/status", new { status });
    public Task<List<string>> GetAgentsAsync() => GetAsync<List<string>>("api/workspace/agents");
    public Task<Dictionary<string, string>> GetAgentIconsAsync() => GetAsync<Dictionary<string, string>>("api/workspace/agents/icons");

    async Task<T> GetAsync<T>(string url)
    {
        using var r = await http.GetAsync(url);
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    async Task<T> PostAsync<T>(string url, object body)
    {
        using var r = await http.PostAsJsonAsync(url, body);
        await RemoteAgentSettingsService.EnsureOkAsync(r, default);
        return r.StatusCode == HttpStatusCode.NoContent ? default! : (await r.Content.ReadFromJsonAsync<T>())!;
    }

    public async ValueTask DisposeAsync() { if (hub != null) await hub.DisposeAsync(); }
}
