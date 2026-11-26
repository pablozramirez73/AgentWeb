using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AgentWeb.Shared.Workspace;
using AgentWeb.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentWeb.Tests;

// Notifiche push: chi le riceve (menzioni, canali privati, task; mai l'autore, mai chi non vede il canale),
// la consegna Web Push (VAPID, contenuto cifrato) e la pulizia dei dispositivi morti o revocati.
public sealed class PushTests
{
    const string Alice = "alice@test.it", Bob = "bob@test.it";

    // Un'iscrizione Web Push valida (chiave P-256 del "browser") verso il finto servizio push.
    static PushDeviceRequest Browser(string endpoint)
    {
        using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = ec.ExportParameters(false).Q;
        return new("web", endpoint, Base64Url.EncodeToString([0x04, .. q.X!, .. q.Y!]), Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)));
    }

    static async Task<int> ChannelAsync(HttpClient http, string name, bool isPrivate = false) =>
        (await (await http.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest(name, isPrivate))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;

    static WorkspaceEvent Message(int channel, string author, string text) => new("message", channel, Message: new(1, channel, author, AuthorKind.User, text, DateTimeOffset.UtcNow));

    [Fact]
    public async Task Recipients_AreMentionsPrivateChannelsAndTasks_NeverTheAuthorOrOutsiders()
    {
        using var app = new TestApp();
        await app.CreateUserAsync(Alice); await app.CreateUserAsync(Bob);
        using var alice = await app.LoginAsync(Alice);
        using var bob = await app.LoginAsync(Bob);
        (await bob.PostAsJsonAsync("/api/push/devices", Browser("https://push.example.com/bob"))).EnsureSuccessStatusCode();
        var pub = await ChannelAsync(alice, "generale");
        var secret = await ChannelAsync(alice, "segreto", isPrivate: true); // bob non è membro
        var team = await ChannelAsync(alice, "team", isPrivate: true);
        (await alice.PostAsJsonAsync($"/api/workspace/channels/{team}/members", new { name = Bob })).EnsureSuccessStatusCode();
        var notifier = app.Services.GetServices<IHostedService>().OfType<PushNotifier>().Single();

        var mention = Assert.Single(await notifier.RecipientsAsync(Message(pub, Alice, "ciao @bob, guardi tu?")));
        Assert.Equal((Bob, "alice ti ha menzionato in #generale", $"channels/{pub}"), (mention.User, mention.Note.Title, mention.Note.Url));
        Assert.Empty(await notifier.RecipientsAsync(Message(pub, Alice, "ciao a tutti")));
        Assert.Empty(await notifier.RecipientsAsync(Message(secret, Alice, "@bob qui non entri"))); // non vede il canale
        Assert.Equal(Bob, Assert.Single(await notifier.RecipientsAsync(Message(team, Alice, "riunione alle 3"))).User);
        Assert.Empty(await notifier.RecipientsAsync(Message(team, Bob, "ok @bob"))); // mai a sé stessi

        var task = new TaskDto(1, pub, "Rivedi il testo", "", Bob, false, WorkStatus.Todo, null, Alice, DateTimeOffset.UtcNow);
        Assert.Equal("Rivedi il testo", Assert.Single(await notifier.RecipientsAsync(new("task", pub, Task: task))).Note.Body);
        Assert.Empty(await notifier.RecipientsAsync(new("task", pub, Task: task with { Status = WorkStatus.Done })));

        // Ogni tipo di avviso si spegne dalle impostazioni.
        Assert.Equal(new NotificationSettingsDto(true, true, true), await bob.GetFromJsonAsync<NotificationSettingsDto>("/api/account/notifications"));
        (await bob.PutAsJsonAsync("/api/account/notifications", new NotificationSettingsDto(false, false, false))).EnsureSuccessStatusCode();
        Assert.Empty(await notifier.RecipientsAsync(Message(pub, Alice, "ciao @bob")));
        Assert.Empty(await notifier.RecipientsAsync(Message(team, Alice, "riunione")));
        Assert.Empty(await notifier.RecipientsAsync(new("task", pub, Task: task)));
    }

    [Fact]
    public async Task ConversationLevel_WinsOverAccountSettings()
    {
        using var app = new TestApp();
        await app.CreateUserAsync(Alice); await app.CreateUserAsync(Bob);
        using var alice = await app.LoginAsync(Alice);
        using var bob = await app.LoginAsync(Bob);
        (await bob.PostAsJsonAsync("/api/push/devices", Browser("https://push.example.com/bob"))).EnsureSuccessStatusCode();
        var pub = await ChannelAsync(alice, "generale");
        var dm = (await (await alice.PostAsJsonAsync("/api/workspace/direct", new { name = Bob })).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        var secret = await ChannelAsync(alice, "segreto", isPrivate: true);
        var notifier = app.Services.GetServices<IHostedService>().OfType<PushNotifier>().Single();
        Task Set(int channel, string level) => bob.PutAsJsonAsync($"/api/workspace/channels/{channel}/notifications", new ChannelNotifyDto(level)).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

        Assert.Equal(NotifyLevels.Default, (await bob.GetFromJsonAsync<ChannelNotifyDto>($"/api/workspace/channels/{dm}/notifications"))!.Level);
        Assert.Single(await notifier.RecipientsAsync(Message(dm, Alice, "ciao")));
        await Set(dm, NotifyLevels.Mute);
        Assert.Equal(NotifyLevels.Mute, (await bob.GetFromJsonAsync<ChannelNotifyDto>($"/api/workspace/channels/{dm}/notifications"))!.Level);
        Assert.Empty(await notifier.RecipientsAsync(Message(dm, Alice, "ciao @bob")));
        await Set(dm, NotifyLevels.Mentions);
        Assert.Empty(await notifier.RecipientsAsync(Message(dm, Alice, "ciao")));
        Assert.Single(await notifier.RecipientsAsync(Message(dm, Alice, "ciao @bob")));
        await Set(pub, NotifyLevels.All); // ogni messaggio, anche in un canale pubblico
        Assert.Single(await notifier.RecipientsAsync(Message(pub, Alice, "ciao a tutti")));
        await Set(dm, NotifyLevels.Default);
        Assert.Single(await notifier.RecipientsAsync(Message(dm, Alice, "ciao")));

        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PutAsJsonAsync($"/api/workspace/channels/{pub}/notifications", new ChannelNotifyDto("forte"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/workspace/channels/{secret}/notifications", new ChannelNotifyDto(NotifyLevels.All))).StatusCode);
    }

    [Fact]
    public async Task Registration_RejectsServicesTheServerDoesNotRun_AndBadEndpoints()
    {
        using var app = new TestApp();
        await app.CreateUserAsync(Alice);
        using var alice = await app.LoginAsync(Alice);
        var config = (await alice.GetFromJsonAsync<PushConfigDto>("/api/push/config"))!;
        Assert.Equal(["web"], config.Kinds); // né Pushy né APNs configurati
        Assert.Equal(87, config.VapidPublicKey.Length); // 65 byte in base64url: chiave generata al primo avvio

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/push/devices", new PushDeviceRequest("pushy", "token"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/push/devices", Browser("http://push.example.com/x"))).StatusCode); // solo https
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/push/devices", new PushDeviceRequest("web", "https://push.example.com/x"))).StatusCode); // senza chiavi
    }

    [Fact]
    public async Task WebPush_IsDeliveredSigned_AndDeadOrRevokedDevicesAreRemoved()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        await using var service = b.Build();
        var hits = new ConcurrentQueue<(string Id, string Auth, string Encoding)>();
        service.MapPost("/{id}", (string id, HttpRequest r) =>
        {
            hits.Enqueue((id, r.Headers.Authorization.ToString(), r.Headers.ContentEncoding.ToString()));
            return Results.StatusCode(id == "gone" ? 410 : 201);
        });
        await service.StartAsync();
        var url = service.Urls.First();

        using var app = new TestApp(new() { ["Web:AllowPrivateHosts"] = "true" });
        await app.CreateUserAsync(Alice);
        using var phone = await app.LoginAsync(Alice);
        using var laptop = await app.LoginAsync(Alice);
        (await phone.PostAsJsonAsync("/api/push/devices", Browser($"{url}/ok"))).EnsureSuccessStatusCode();
        (await laptop.PostAsJsonAsync("/api/push/devices", Browser($"{url}/gone"))).EnsureSuccessStatusCode();

        Assert.Equal(1, await (await phone.PostAsync("/api/push/test", null)).Content.ReadFromJsonAsync<int>());
        Assert.Contains(hits, h => h.Id == "ok" && h.Auth.StartsWith("vapid t=") && h.Encoding == "aes128gcm");
        Assert.Contains(hits, h => h.Id == "gone");

        await using var db = await app.Services.GetRequiredService<IDbContextFactory<AppDb>>().CreateDbContextAsync();
        var left = Assert.Single(await db.PushDevices.ToListAsync()); // il 410 ha cancellato il dispositivo morto
        Assert.EndsWith("/ok", left.Token);

        // Revocare la sessione (telefono perso) spegne anche le sue notifiche.
        Assert.True(await app.Services.GetRequiredService<SessionStore>().RevokeAsync(left.Sid!, Alice));
        Assert.Empty(await db.PushDevices.ToListAsync());
    }
}
