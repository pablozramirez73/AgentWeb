using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentWeb.Shared.Workspace;
using dotAPNS;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgentWeb.Web;

public sealed record PushNote(string Title, string Body, string Url);

// Invio delle notifiche push, senza Firebase:
// - "web": Web Push con chiavi VAPID (browser e PWA; generate al primo avvio se non configurate),
// - "apns": iPhone, direttamente ad Apple con la chiave .p8 (Push:Apns:KeyId/TeamId/BundleId/Key),
// - "pushy": Android tramite pushy.me (Push:Pushy:ApiKey).
// I dispositivi che il servizio dichiara morti (scaduti, disinstallati) si cancellano.
public sealed partial class PushSender
{
    readonly IDbContextFactory<AppDb> dbf;
    readonly ILogger<PushSender> log;
    readonly VapidAuthentication vapid;
    readonly PushServiceClient webPush;
    readonly IApnsClient? apns;
    readonly string? pushyKey;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string VapidPublicKey { get; }
    internal bool AllowHttpEndpoints { get; } // solo i test: il finto servizio push è http://127.0.0.1
    public List<string> Kinds { get; } = ["web"];

    public PushSender(IDbContextFactory<AppDb> dbf, IConfiguration config, IWebHostEnvironment env, ILogger<PushSender> log)
    {
        this.dbf = dbf; this.log = log;
        var (pub, priv) = VapidKeys(config, env);
        VapidPublicKey = pub;
        vapid = new VapidAuthentication(pub, priv) { Subject = config["Push:Vapid:Subject"] ?? "mailto:noreply@agentweb.dev" };
        var allowPrivate = AllowHttpEndpoints = config.GetValue("Web:AllowPrivateHosts", false); // solo i test (finto servizio push su 127.0.0.1)
        // L'endpoint Web Push lo sceglie il browser (cioè il client): mai verso indirizzi interni del server.
        webPush = new PushServiceClient(new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (ctx, ct) =>
            {
                var allowed = (await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct)).Where(a => allowPrivate || SafeFetch.IsPublic(a)).ToArray();
                if (allowed.Length == 0) throw new HttpRequestException($"indirizzo non pubblico: {ctx.DnsEndPoint.Host}");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            },
        }) { Timeout = TimeSpan.FromSeconds(15) });

        if (config["Push:Apns:Key"] is { Length: > 0 } p8)
        {
            var client = ApnsClient.CreateUsingJwt(new HttpClient(), new ApnsJwtOptions
            {
                CertContent = p8, KeyId = config["Push:Apns:KeyId"], TeamId = config["Push:Apns:TeamId"], BundleId = config["Push:Apns:BundleId"],
            });
            apns = config.GetValue("Push:Apns:Sandbox", false) ? client.UseSandbox() : client; // Sandbox: app installate da Xcode
            Kinds.Add("apns");
        }
        if (config["Push:Pushy:ApiKey"] is { Length: > 0 } key) { pushyKey = key; Kinds.Add("pushy"); }
    }

    // Le chiavi VAPID devono restare le stesse, altrimenti le iscrizioni dei browser smettono di funzionare:
    // dalla configurazione, oppure generate una volta e salvate accanto ad agentsettings.json (volume persistente).
    static (string Public, string Private) VapidKeys(IConfiguration config, IWebHostEnvironment env)
    {
        if (config["Push:Vapid:PublicKey"] is { Length: > 0 } pub && config["Push:Vapid:PrivateKey"] is { Length: > 0 } priv) return (pub, priv);
        var settings = config["AgentSettings:Path"] ?? Path.Combine(env.ContentRootPath, "agentsettings.json");
        var file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings))!, "vapid-keys.json");
        if (File.Exists(file) && JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) is [var p, var k]) return (p, k);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var e = ec.ExportParameters(includePrivateParameters: true);
        string[] keys = [Base64Url.EncodeToString([0x04, .. e.Q.X!, .. e.Q.Y!]), Base64Url.EncodeToString(e.D)];
        File.WriteAllText(file, JsonSerializer.Serialize(keys));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return (keys[0], keys[1]);
    }

    // Ritorna quanti dispositivi hanno accettato la notifica.
    public async Task<int> SendAsync(string userName, PushNote note, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var devices = await db.PushDevices.Where(d => d.UserName == userName).ToListAsync(ct);
        var payload = JsonSerializer.Serialize(new { title = note.Title, body = note.Body, url = note.Url });
        var dead = new List<long>();
        var sent = 0;
        foreach (var d in devices.Where(d => d.Kind != "pushy"))
        {
            try
            {
                if (d.Kind == "web") await SendWebAsync(d, payload, ct);
                else if (d.Kind == "apns" && apns != null)
                {
                    var r = await apns.SendAsync(new ApplePush(ApplePushType.Alert).AddAlert(note.Title, note.Body).AddToken(d.Token).AddSound().AddCustomProperty("url", note.Url), ct);
                    if (!r.IsSuccessful)
                    {
                        if (r.Reason is ApnsResponseReason.BadDeviceToken or ApnsResponseReason.Unregistered or ApnsResponseReason.DeviceTokenNotForTopic) dead.Add(d.Id);
                        else log.LogWarning("APNs ha rifiutato la notifica: {Reason}", r.ReasonString);
                        continue;
                    }
                }
                else continue;
                sent++;
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { dead.Add(d.Id); }
            catch (Exception ex) { log.LogWarning(ex, "Notifica {Kind} non inviata a {User}", d.Kind, userName); }
        }
        if (pushyKey != null && devices.Where(d => d.Kind == "pushy").ToList() is { Count: > 0 } android)
            sent += await SendPushyAsync(android, note, dead, ct);
        if (dead.Count > 0) await db.PushDevices.Where(d => dead.Contains(d.Id)).ExecuteDeleteAsync(ct);
        return sent;
    }

    Task SendWebAsync(PushDeviceEntity d, string payload, CancellationToken ct)
    {
        var sub = new PushSubscription { Endpoint = d.Token, Keys = new Dictionary<string, string> { ["p256dh"] = d.P256dh!, ["auth"] = d.Auth! } };
        return webPush.RequestPushMessageDeliveryAsync(sub, new PushMessage(payload) { TimeToLive = 24 * 3600, Urgency = PushMessageUrgency.High }, vapid, ct);
    }

    // API REST di Pushy: i token sconosciuti tornano in info.failed (o NO_RECIPIENTS se non ce n'è nessuno valido).
    async Task<int> SendPushyAsync(List<PushDeviceEntity> devices, PushNote note, List<long> dead, CancellationToken ct)
    {
        try
        {
            using var r = await http.PostAsJsonAsync($"https://api.pushy.me/push?api_key={Uri.EscapeDataString(pushyKey!)}",
                new { to = devices.Select(d => d.Token).ToArray(), data = new { title = note.Title, body = note.Body, url = note.Url }, time_to_live = 24 * 3600 }, ct);
            var body = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (!r.IsSuccessStatusCode)
            {
                if (body.TryGetProperty("code", out var code) && code.GetString() == "NO_RECIPIENTS") dead.AddRange(devices.Select(d => d.Id));
                else log.LogWarning("Pushy ha rifiutato la notifica: {Body}", body);
                return 0;
            }
            var failed = body.TryGetProperty("info", out var info) && info.TryGetProperty("failed", out var f) ? f.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
            dead.AddRange(devices.Where(d => failed.Contains(d.Token)).Select(d => d.Id));
            return devices.Count - failed.Count;
        }
        catch (Exception ex) { log.LogWarning(ex, "Notifica Pushy non inviata"); return 0; }
    }
}

// Chi riceve cosa: menzioni (@nome), messaggi nei canali privati, task assegnati a una persona.
// Mai all'autore, mai a chi non vede il canale, solo se l'utente ha lasciato attivo quel tipo di avviso.
public sealed partial class PushNotifier(WorkspaceBus bus, IDbContextFactory<AppDb> dbf, PushSender sender, ILogger<PushNotifier> log) : IHostedService
{
    public Task StartAsync(CancellationToken ct) { bus.Published += OnEvent; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { bus.Published -= OnEvent; return Task.CompletedTask; }

    void OnEvent(WorkspaceEvent e)
    {
        if (e.Kind is not ("message" or "task")) return;
        _ = Task.Run(async () => // il bus è sincrono: chi pubblica non aspetta i servizi push
        {
            try { foreach (var (user, note) in await RecipientsAsync(e)) await sender.SendAsync(user, note); }
            catch (Exception ex) { log.LogWarning(ex, "Notifiche push non inviate per l'evento {Kind}", e.Kind); }
        });
    }

    public async Task<List<(string User, PushNote Note)>> RecipientsAsync(WorkspaceEvent e)
    {
        await using var db = await dbf.CreateDbContextAsync();
        if (await db.Channels.Where(c => c.Id == e.ChannelId).Select(c => new { c.Name, c.IsPrivate, c.DirectKey }).FirstOrDefaultAsync() is not { } ch) return [];
        // Solo chi ha almeno un dispositivo registrato: gli altri non riceverebbero comunque niente.
        var users = await db.Users.Where(u => db.PushDevices.Any(d => d.UserName == u.UserName))
            .Select(u => new { Name = u.UserName!, u.NotifyMentions, u.NotifyPrivate, u.NotifyTasks }).ToListAsync();
        var url = $"channels/{e.ChannelId}";
        var result = new List<(string, PushNote)>();

        if (e is { Kind: "message", Message: { Kind: not AuthorKind.System } m })
        {
            var mentioned = MentionRegex().Matches(m.Text).Select(x => x.Groups[1].Value.TrimEnd('.', '-')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var body = m.Text.Length > 140 ? m.Text[..140] + "…" : m.Text;
            foreach (var u in users.Where(u => u.Name != m.Author))
            {
                var mention = u.NotifyMentions && mentioned.Contains(Handle(u.Name));
                if (!mention && !(u.NotifyPrivate && ch.IsPrivate)) continue;
                if (!await ChannelVisibility.CanAccessAsync(db, e.ChannelId, u.Name)) continue;
                var title = ch.DirectKey is not null ? $"{Handle(m.Author)} ti ha scritto"
                    : mention ? $"{Handle(m.Author)} ti ha menzionato in #{ch.Name}" : $"#{ch.Name} · {Handle(m.Author)}";
                result.Add((u.Name, new(title, body, ch.DirectKey is not null ? "messages" : url)));
            }
        }
        else if (e is { Kind: "task", Task: { AssigneeIsAgent: false, Assignee: { } assignee, Status: WorkStatus.Todo } t } && assignee != t.CreatedBy
            && users.FirstOrDefault(u => u.Name == assignee) is { NotifyTasks: true }
            && await ChannelVisibility.CanAccessAsync(db, e.ChannelId, assignee))
            result.Add((assignee, new($"Nuovo task da {Handle(t.CreatedBy)} in #{ch.Name}", t.Title, url)));
        return result;
    }

    static string Handle(string user) => user.Split('@')[0];

    [GeneratedRegex(@"@([\p{L}\p{N}_.-]+)")]
    private static partial Regex MentionRegex();
}

public static class PushEndpoints
{
    public const int MaxDevicesPerUser = 20;

    public static async Task<NotificationSettingsDto> GetSettingsAsync(AppDb db, string user) =>
        await db.Users.Where(u => u.UserName == user).Select(u => new NotificationSettingsDto(u.NotifyMentions, u.NotifyPrivate, u.NotifyTasks)).FirstAsync();

    public static Task SaveSettingsAsync(AppDb db, string user, NotificationSettingsDto s) =>
        db.Users.Where(u => u.UserName == user).ExecuteUpdateAsync(x => x
            .SetProperty(u => u.NotifyMentions, s.Mentions).SetProperty(u => u.NotifyPrivate, s.Private).SetProperty(u => u.NotifyTasks, s.Tasks));

    // Stesso token = stesso dispositivo: se ora lo usa un altro account (logout e login di un altro), passa a quello.
    public static async Task RegisterAsync(AppDb db, PushSender sender, string user, string? sid, PushDeviceRequest d)
    {
        if (!sender.Kinds.Contains(d.Kind)) throw new ArgumentException($"Notifiche \"{d.Kind}\" non attive sul server.");
        if (string.IsNullOrWhiteSpace(d.Token) || d.Token.Length > 1000) throw new ArgumentException("Token del dispositivo non valido.");
        if (d.Kind == "web" && (!Uri.TryCreate(d.Token, UriKind.Absolute, out var endpoint) || !(endpoint.Scheme == "https" || sender.AllowHttpEndpoints && endpoint.Scheme == "http")
            || string.IsNullOrEmpty(d.P256dh) || string.IsNullOrEmpty(d.Auth) || d.P256dh.Length > 200 || d.Auth.Length > 100))
            throw new ArgumentException("Iscrizione Web Push non valida.");
        var device = await db.PushDevices.FirstOrDefaultAsync(x => x.Token == d.Token);
        if (device is null)
        {
            if (await db.PushDevices.CountAsync(x => x.UserName == user) >= MaxDevicesPerUser)
                throw new ArgumentException($"Al massimo {MaxDevicesPerUser} dispositivi: disattiva le notifiche su uno vecchio.");
            db.PushDevices.Add(device = new() { Token = d.Token, CreatedAt = DateTimeOffset.UtcNow });
        }
        (device.UserName, device.Sid, device.Kind, device.P256dh, device.Auth) = (user, sid, d.Kind, d.P256dh, d.Auth);
        await db.SaveChangesAsync();
    }

    public static Task UnregisterAsync(AppDb db, string user, string token) =>
        db.PushDevices.Where(d => d.UserName == user && d.Token == token).ExecuteDeleteAsync();

    public static void MapPushEndpoints(this WebApplication app)
    {
        static string Me(ClaimsPrincipal u) => u.Identity!.Name!;
        var me = app.MapGroup("/api").RequireAuthorization();
        me.MapGet("/account/notifications", async (ClaimsPrincipal u, AppDb db) => await GetSettingsAsync(db, Me(u)));
        me.MapPut("/account/notifications", async (NotificationSettingsDto s, ClaimsPrincipal u, AppDb db) => { await SaveSettingsAsync(db, Me(u), s); return Results.NoContent(); });
        me.MapGet("/push/config", (PushSender s) => new PushConfigDto(s.VapidPublicKey, s.Kinds));
        me.MapPost("/push/devices", async (PushDeviceRequest d, ClaimsPrincipal u, AppDb db, PushSender s) =>
        {
            try { await RegisterAsync(db, s, Me(u), u.FindFirstValue(SessionStore.Claim), d); return Results.NoContent(); }
            catch (ArgumentException ex) { return Results.Text(ex.Message, statusCode: 400); }
        });
        me.MapPost("/push/devices/remove", async (PushTokenRequest r, ClaimsPrincipal u, AppDb db) => { await UnregisterAsync(db, Me(u), r.Token); return Results.NoContent(); });
        me.MapPost("/push/test", async (ClaimsPrincipal u, PushSender s) =>
            await s.SendAsync(Me(u), new("AgentWeb", "Le notifiche funzionano su questo dispositivo.", "notifications")))
            .RequireRateLimiting(SessionEndpoints.AuthRateLimit);
    }
}
