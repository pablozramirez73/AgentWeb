namespace AgentWeb.Shared.Workspace;

// Unread: messaggi altrui dopo l'ultimo letto; Mentions: tra questi, quelli che citano @<utente> (parte prima della @ dell'email).
// With: nei messaggi diretti, l'altro partecipante (email della persona o nome dell'agente); null nei canali normali.
public sealed record ChannelDto(int Id, string Name, bool IsPrivate = false, string? CreatedBy = null, int Unread = 0, int Mentions = 0, string? With = null);

// Agenti del canale (rispondono alle @menzioni, ricevono task, collaborano qui), quelli che si possono ancora aggiungere
// e se l'utente può gestirli (solo chi ha creato il canale).
public sealed record ChannelAgentsDto(List<string> Agents, List<string> Available, bool CanManage);

public enum AuthorKind { User, Agent, System }

// ParentId: il messaggio radice del thread (null = messaggio del canale). I thread hanno un solo livello.
public sealed record MessageDto(long Id, int ChannelId, string Author, AuthorKind Kind, string Text, DateTimeOffset At, long? ParentId = null);

// Risultato della ricerca: solo nei canali che l'utente vede.
public sealed record SearchHitDto(MessageDto Message, string ChannelName);

public sealed record MarkReadRequest(long LastId);

// Backlog e Closed in fondo: i valori già salvati nel DB (interi) non cambiano significato.
// Backlog = parcheggiato (gli agenti non lo prendono); Closed = annullato senza farlo.
public enum WorkStatus { Todo, InProgress, Done, Failed, Backlog, Closed }

// Pagina Attività: lavori degli agenti in corso e ultimi terminati (Seconds/Error), task aperti.
public sealed record AgentRunDto(string Agent, int ChannelId, string Channel, string Kind, DateTimeOffset StartedAt, double? Seconds, string? Error);
// Pagina Statistiche: ultimi Days giorni (UTC), solo canali visibili. Errors: gli ultimi lavori falliti.
public sealed record StatsDto(int Days, List<DayStatDto> Daily, List<AgentStatDto> Agents, List<ModelStatDto> Models,
    List<AgentRunDto> Errors, List<StatusCountDto> Tasks);
public sealed record DayStatDto(DateOnly Day, int Runs, int Errors, long InputTokens, long OutputTokens, int Messages);
public sealed record AgentStatDto(string Agent, int Runs, int Errors, long InputTokens, long OutputTokens, double AvgSeconds, double MaxSeconds);
public sealed record ModelStatDto(string Model, int Runs, long InputTokens, long OutputTokens);
public sealed record StatusCountDto(WorkStatus Status, int Count);
public sealed record ActivityDto(List<AgentRunDto> Running, List<AgentRunDto> Recent, List<TaskDto> OpenTasks);
// Statistiche → Agenti: un agente negli ultimi Days giorni, solo canali visibili, più recenti per primi (max 200 per elenco).
// Messages: scritti da lui, che lo @menzionano o che gli delegano lavoro ("→ chiedo a <agente>:").
public sealed record AgentLogDto(string Agent, List<RunLogDto> Runs, List<SearchHitDto> Messages);
public sealed record RunLogDto(int ChannelId, string Channel, string Kind, DateTimeOffset StartedAt, double Seconds, string? Error,
    string? Model, long InputTokens, long OutputTokens);

// Pagina Attività (inbox): una voce per canale (Root = null) o per thread che ti riguarda (scritto, risposto o menzionato).
// Last: l'ultimo messaggio (del canale fuori dai thread, oppure del thread). Unread: messaggi altrui non ancora letti.
public sealed record FeedItemDto(int ChannelId, string Channel, bool IsPrivate, MessageDto? Root, MessageDto Last, int Replies, int Unread, string? With = null);

// MessageId: il messaggio da cui nasce il task (il suo thread è la conversazione del task); Agent: l'agente che ci lavora,
// anche quando ha passato il task a una persona per una decisione (una risposta nel thread glielo restituisce).
public sealed record TaskDto(int Id, int ChannelId, string Title, string Description, string? Assignee, bool AssigneeIsAgent,
    WorkStatus Status, string? Result, string CreatedBy, DateTimeOffset UpdatedAt, long? MessageId = null, string? Agent = null);

public sealed record NewTaskRequest(string Title, string Description, string? Assignee)
{
    public const string Me = "\u0001me"; // Assignee speciale: il server lo sostituisce con l'utente che crea il task
}

// Un solo tipo di evento in tempo reale: Kind = "channel" | "message" | "task" | "typing" | "agents" | "resync".
// "message" arriva per ogni canale che l'utente vede (una volta sola): quello osservato lo mostra, gli altri aggiornano i badge dei non letti.
// "typing": Agent = chi sta scrivendo/lavorando, Human = true se è una persona. "agents": cambiati gli agenti del canale.
// "resync" lo genera il client dopo una riconnessione lunga: la pagina recupera i messaggi persi.
public sealed record WorkspaceEvent(string Kind, int ChannelId, MessageDto? Message = null, TaskDto? Task = null,
    ChannelDto? Channel = null, string? Agent = null, bool Typing = false, bool Human = false);

// Replay dopo una disconnessione: i messaggi con Id > AfterId. Reset = il buco supera l'orizzonte, ricaricare da capo.
public sealed record ReplayDto(List<MessageDto> Messages, bool Reset);

public sealed record NewMessageRequest(string Text, string? ClientId = null, long? ParentId = null); // ClientId: idempotenza dei retry; ParentId: risposta in un thread

// Memoria del canale vista dalle persone: ricordi, riassunti e documenti caricati (un documento = una voce, Chunks pezzi).
public sealed record MemoryDto(long Id, string Kind, string? Title, string Content, string CreatedBy, DateTimeOffset CreatedAt, string? SourceId, int Chunks);

// Risposte dei metodi dell'hub (/hubs/workspace): Error valorizzato al posto di un'eccezione, così il testo arriva intatto.
// NotFound = canale inesistente o non visibile (come il 404 dell'API HTTP).
public sealed record HubWatchReply(bool NotFound, ReplayDto? Replay, string? Error = null);
// Solo l'id: il messaggio completo arriva comunque a tutti come evento, inutile rimandarlo a chi l'ha scritto.
public sealed record HubSendReply(long? MessageId, bool NotFound = false, string? Error = null);

public sealed record NewChannelRequest(string Name, bool IsPrivate = false);

// Implementato in-process dal server web e via HTTP + SignalR dall'app MAUI e dal browser (WebAssembly).
// Visibilità: un canale privato esiste solo per i suoi membri (chi non lo vede riceve 404, non 403).
public interface IWorkspaceClient
{
    event Action<WorkspaceEvent>? Changed;
    Task StartAsync();
    // Eventi di messaggi/task/typing arrivano solo per il canale osservato (null = nessuno).
    // afterId >= 0: nella stessa chiamata restituisce anche i messaggi successivi (recupero), senza una richiesta in più.
    // KeyNotFoundException se il canale non è visibile.
    Task<ReplayDto?> WatchChannelAsync(int? channelId, long afterId = -1);
    Task SetTypingAsync(int channelId, bool typing); // "sta scrivendo…" per gli altri che guardano il canale
    Task<ChannelAgentsDto> GetChannelAgentsAsync(int channelId);
    Task AddChannelAgentAsync(int channelId, string agent);    // solo chi ha creato il canale
    Task RemoveChannelAgentAsync(int channelId, string agent);
    Task<List<MemoryDto>> GetChannelMemoryAsync(int channelId);
    Task UploadDocumentAsync(int channelId, string fileName, string text); // testo (.txt, .md, .csv, .json…), max 1 MB
    Task DeleteMemoryAsync(int channelId, long memoryId);
    Task DeleteDocumentAsync(int channelId, string sourceId);
    Task<List<ChannelDto>> GetChannelsAsync();
    Task<ChannelDto> CreateChannelAsync(string name, bool isPrivate = false);
    Task<List<string>> GetMembersAsync(int channelId);
    Task AddMemberAsync(int channelId, string userName);
    Task<List<MessageDto>> GetMessagesAsync(int channelId);
    Task<ReplayDto> GetMessagesSinceAsync(int channelId, long afterId);
    Task SendMessageAsync(int channelId, string text, long? parentId = null); // parentId: risposta nel thread di quel messaggio
    Task MarkReadAsync(int channelId, long lastId); // il cursore avanza soltanto
    Task<List<SearchHitDto>> SearchAsync(string query);
    Task<string> WhoAmIAsync(); // utente corrente (per riconoscere le proprie @menzioni)
    Task<ActivityDto> GetActivityAsync(); // cosa stanno facendo gli agenti, solo nei canali visibili
    Task<StatsDto> GetStatsAsync(int days); // token, errori e lavori degli ultimi giorni, solo nei canali visibili
    Task<AgentLogDto> GetAgentLogAsync(string agent, int days); // lavori e messaggi di un agente, solo nei canali visibili
    Task<List<FeedItemDto>> GetFeedAsync(); // inbox della pagina Attività, più recenti per primi
    Task<List<MessageDto>> GetThreadAsync(int channelId, long rootId);
    Task MarkThreadReadAsync(int channelId, long rootId, long lastId); // segna letto il thread senza toccare il resto del canale
    Task<List<TaskDto>> GetTasksAsync(int channelId);
    Task<List<TaskDto>> GetAllTasksAsync();
    Task<ChannelNotifyDto> GetChannelNotifyAsync(int channelId);
    Task SetChannelNotifyAsync(int channelId, string level); // NotifyLevels
    Task<ChannelDto> OpenDirectAsync(string with); // conversazione diretta con una persona (email) o un agente: la crea la prima volta
    Task<List<string>> GetPeopleAsync();          // persone viste nei canali che l'utente vede (per scegliere a chi scrivere) // pagina Task: tutti i task dei canali visibili, aggiornati di recente per primi
    Task<TaskDto> CreateTaskAsync(int channelId, NewTaskRequest task);
    Task<TaskDto> CreateTaskFromMessageAsync(int channelId, long messageId); // lo prende l'agente menzionato (o l'unico del canale)
    Task<TaskDto> SendAsTaskAsync(int channelId, string text);              // "Come task": messaggio e task insieme
    Task SetTaskStatusAsync(int taskId, WorkStatus status);
    Task<List<string>> GetAgentsAsync();
    Task<Dictionary<string, string>> GetAgentIconsAsync(); // nome agente -> SVG quadrato (da mostrare solo come <img>)
}

// Sessioni dell'utente (una per login: web, app, CLI). Revocarne una la chiude subito ovunque.
public sealed record SessionDto(string Id, string Kind, string? Device, DateTimeOffset CreatedAt, bool Current);

public sealed record AccountInfo(string UserName, bool IsAdmin, string? SessionId);

// Token API per agenti esterni (Claude, Hermes… tramite il server MCP /mcp). Token = il segreto, mostrato una volta sola.
public sealed record ApiTokenDto(string Id, string Name, string Token);

public sealed record NewApiTokenRequest(string Name);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

// Verifica in due passaggi (TOTP, app tipo Authenticator). SharedKey/AuthenticatorUri solo finché non è attiva.
public sealed record TwoFactorDto(bool Enabled, string? SharedKey, string? AuthenticatorUri);

public sealed record TwoFactorCodeRequest(string Code);

// Profilo: Avatar è un data URL (data:image/...;base64,...) pronto per <img>; Providers = accessi esterni configurati sul server.
public sealed record ProfileDto(string UserName, string? DisplayName, string? Avatar, bool EmailConfirmed, bool CanSendEmail,
    List<ExternalLoginDto> Logins, List<string> Providers);

// Notifiche push. Kind del dispositivo: "web" (Web Push, Token = endpoint), "apns" (iPhone), "pushy" (Android).
public sealed record NotificationSettingsDto(bool Mentions, bool Private, bool Tasks);
// Notifiche di una sola conversazione. Default ("") = segue le impostazioni dell'account.
public sealed record ChannelNotifyDto(string Level);
public static class NotifyLevels
{
    public const string Default = "", All = "all", Mentions = "mentions", Mute = "mute";
    public static readonly string[] Values = [Default, All, Mentions, Mute];
}
public sealed record PushDeviceRequest(string Kind, string Token, string? P256dh = null, string? Auth = null);
public sealed record PushTokenRequest(string Token);
public sealed record PushConfigDto(string VapidPublicKey, List<string> Kinds); // Kinds: i servizi attivi sul server

public sealed record ExternalLoginDto(string Provider, string? Account);

public sealed record ProfileRequest(string? DisplayName);

public sealed record AvatarRequest(string ContentType, string Base64);

// Implementato in-process dal server web e via HTTP da browser (WebAssembly) e app MAUI.
public interface IAccountClient
{
    Task<ProfileDto> GetProfileAsync();
    Task SaveProfileAsync(string? displayName);
    Task SetAvatarAsync(string? contentType, byte[]? data); // null = rimuove la foto
    Task SendConfirmationEmailAsync();
    Task RemoveLoginAsync(string provider);
    Task<NotificationSettingsDto> GetNotificationSettingsAsync();
    Task SaveNotificationSettingsAsync(NotificationSettingsDto settings);
    Task<PushConfigDto> GetPushConfigAsync();
    Task RegisterPushDeviceAsync(PushDeviceRequest device);
    Task UnregisterPushDeviceAsync(string token);
    Task<int> SendTestPushAsync(); // dispositivi raggiunti
    Task<List<SessionDto>> GetSessionsAsync();
    Task RevokeSessionAsync(string id);
    Task<int> RevokeAllSessionsAsync();
    Task<ApiTokenDto> CreateTokenAsync(string name);
    Task ChangePasswordAsync(string currentPassword, string newPassword); // chiude le altre sessioni web/app
    Task<TwoFactorDto> GetTwoFactorAsync();
    Task EnableTwoFactorAsync(string code);  // il codice prova che l'app è configurata
    Task DisableTwoFactorAsync(string code);
    Uri ServerBase { get; } // indirizzo del server, per mostrare l'URL dell'endpoint MCP (/mcp)
}

public interface IUserSession
{
    event Action? Changed;
    bool NeedsLogin { get; }
    string? UserName { get; }
    Task InitializeAsync();
    Task<string?> LoginAsync(string email, string password, string? code = null); // null = ok, altrimenti messaggio d'errore; code = TOTP
    Task<string?> RegisterAsync(string email, string password);
    Task<bool> CanRegisterAsync(); // vero solo finché il server non ha account (il primo diventa admin)
    Task LogoutAsync();
}
