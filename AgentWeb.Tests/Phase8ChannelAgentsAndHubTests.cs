using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Workspace;
using AgentWeb.Web;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentWeb.Tests;

// Agenti del canale: li sceglie chi ha creato il canale; solo loro rispondono, ricevono task e collaborano lì.
public sealed class Phase8ChannelAgentsTests : IAsyncLifetime
{
    TestApp app = null!;
    FakeLlm llm = null!;
    HttpClient alice = null!, bob = null!;
    int channel;

    public async Task InitializeAsync()
    {
        llm = await FakeLlm.StartAsync("risposta dell'esperto");
        app = new TestApp();
        LlmProviderConfig Llm(string model) => new() { Kind = LlmKinds.OpenAI, Url = llm.Url + "/v1", ApiKey = "k", DefaultModel = model };
        var settings = new AgentSettings
        {
            LocalAgents = [new() { Name = "Capo", Prompt = "Coordini.", Llm = Llm("capo") }, new() { Name = "Esperto", Prompt = "Esperto.", Llm = Llm("esperto") }],
        };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(settings));
        await app.CreateUserAsync("alice@test.it");
        await app.CreateUserAsync("bob@test.it");
        alice = await app.LoginAsync("alice@test.it");
        bob = await app.LoginAsync("bob@test.it");
        channel = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("squadra"))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
    }

    public async Task DisposeAsync() { alice.Dispose(); bob.Dispose(); app.Dispose(); await llm.DisposeAsync(); }

    Task<HttpResponseMessage> AddAgent(HttpClient who, string agent) => who.PostAsJsonAsync($"/api/workspace/channels/{channel}/agents", new { name = agent });
    async Task<List<MessageDto>> Messages() => (await alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{channel}/messages"))!;

    async Task<List<MessageDto>> WaitForAsync(Func<List<MessageDto>, bool> done)
    {
        for (var i = 0; i < 100; i++)
        {
            var m = await Messages();
            if (done(m)) return m;
            await Task.Delay(100);
        }
        return await Messages();
    }

    [Fact]
    public async Task OnlyTheCreator_ChoosesTheAgents()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await AddAgent(bob, "Esperto")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AddAgent(alice, "Esperto")).StatusCode);

        var forAlice = (await alice.GetFromJsonAsync<ChannelAgentsDto>($"/api/workspace/channels/{channel}/agents"))!;
        var forBob = (await bob.GetFromJsonAsync<ChannelAgentsDto>($"/api/workspace/channels/{channel}/agents"))!;
        Assert.Equal(["Esperto"], forAlice.Agents);
        Assert.Equal(["Capo"], forAlice.Available);
        Assert.True(forAlice.CanManage);
        Assert.False(forBob.CanManage);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/api/workspace/channels/{channel}/agents/Esperto")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/workspace/channels/{channel}/agents/Esperto")).StatusCode);
        Assert.Empty((await alice.GetFromJsonAsync<ChannelAgentsDto>($"/api/workspace/channels/{channel}/agents"))!.Agents);
    }

    [Fact]
    public async Task MentioningAnAgentNotInTheChannel_ExplainsWhyItIsSilent()
    {
        await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages", new NewMessageRequest("@Esperto ci sei?"));
        var messages = await WaitForAsync(m => m.Any(x => x.Kind == AuthorKind.System && x.Text.Contains("non è in questo canale")));
        Assert.Contains(messages, m => m.Text == "@Esperto non è in questo canale: chi l'ha creato può aggiungerlo.");

        (await AddAgent(alice, "Esperto")).EnsureSuccessStatusCode();
        await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages", new NewMessageRequest("@Esperto e adesso?"));
        messages = await WaitForAsync(m => m.Any(x => x.Author == "Esperto"));
        Assert.Contains(messages, m => m.Author == "Esperto" && m.Text == "risposta dell'esperto");
    }

    [Fact]
    public async Task ATask_CanOnlyGoToAnAgentOfTheChannel()
    {
        var r = await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/tasks", new NewTaskRequest("x", "", "Esperto"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("non è in questo canale", await r.Content.ReadAsStringAsync());
    }

    // Capo prova a delegare a Esperto, che non è nel canale: la delega viene rifiutata e Esperto non lavora.
    [Fact]
    public async Task Delegation_StaysInsideTheChannelTeam()
    {
        (await AddAgent(alice, "Capo")).EnsureSuccessStatusCode();
        var task = (await (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/tasks", new NewTaskRequest("analisi", "", "Capo"))).Content.ReadFromJsonAsync<TaskDto>())!;

        TaskDto? done = null;
        for (var i = 0; i < 100 && done is null; i++)
        {
            await Task.Delay(100);
            done = (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{channel}/tasks"))!.FirstOrDefault(t => t.Id == task.Id && t.Status == WorkStatus.Done);
        }
        Assert.NotNull(done);
        Assert.Contains("non è un agente di questo canale", done.Result);
        Assert.DoesNotContain(await Messages(), m => m.Author == "Esperto");
        Assert.DoesNotContain(llm.Requests, r => r.EndsWith(" esperto"));
    }
}

// Percorsi frequenti sull'hub: invio, iscrizione con recupero, "sta scrivendo". Stesse regole dell'API HTTP.
public sealed class Phase8HubPathTests
{
    static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    sealed record World(TestApp App, HttpClient Alice, string AliceToken, string BobToken, int Private, int Public) : IDisposable
    {
        public void Dispose() { Alice.Dispose(); App.Dispose(); }
    }

    static async Task<World> WorldAsync()
    {
        var app = new TestApp();
        await app.CreateUserAsync("alice@test.it");
        await app.CreateUserAsync("bob@test.it");
        var aliceToken = (await app.LoginTokensAsync("alice@test.it")).AccessToken;
        var bobToken = (await app.LoginTokensAsync("bob@test.it")).AccessToken;
        var alice = app.Bearer(aliceToken);
        async Task<int> Create(string n, bool p) => (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest(n, p))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        return new(app, alice, aliceToken, bobToken, await Create("riservato", true), await Create("aperto", false));
    }

    [Fact]
    public async Task SendMessage_OverTheHub_IsAuthorizedAndIdempotent()
    {
        using var w = await WorldAsync();
        await using var alice = await w.App.ConnectAsync(w.AliceToken, messagePack: true);
        await using var bob = await w.App.ConnectAsync(w.BobToken);

        var first = await alice.Hub.InvokeAsync<HubSendReply>("SendMessage", w.Private, "via hub", "id-1");
        var again = await alice.Hub.InvokeAsync<HubSendReply>("SendMessage", w.Private, "via hub", "id-1");
        Assert.NotNull(first.MessageId);
        Assert.Equal(first.MessageId, again.MessageId); // stesso ClientId: un solo messaggio

        Assert.True((await bob.Hub.InvokeAsync<HubSendReply>("SendMessage", w.Private, "intrusione", "id-2")).NotFound);
        var tooLong = await alice.Hub.InvokeAsync<HubSendReply>("SendMessage", w.Public, new string('x', 5000), "id-3");
        Assert.Contains("massimo", tooLong.Error);

        var stored = (await w.Alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{w.Private}/messages"))!;
        Assert.Single(stored, m => m.Text == "via hub");
    }

    [Fact]
    public async Task WatchFrom_SubscribesAndReturnsWhatWasMissed_InOneCall()
    {
        using var w = await WorldAsync();
        var last = (await w.Alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{w.Public}/messages"))!.Last().Id;
        await w.Alice.PostAsJsonAsync($"/api/workspace/channels/{w.Public}/messages", new NewMessageRequest("perso"));

        await using var alice = await w.App.ConnectAsync(w.AliceToken);
        var reply = await alice.Hub.InvokeAsync<HubWatchReply>("WatchFrom", w.Public, last);
        Assert.False(reply.NotFound);
        Assert.Equal(["perso"], reply.Replay!.Messages.Select(m => m.Text));

        await w.Alice.PostAsJsonAsync($"/api/workspace/channels/{w.Public}/messages", new NewMessageRequest("dal vivo"));
        Assert.Single(await alice.CollectAsync(e => e.Kind == "message" && e.Message?.Text == "dal vivo", Window)); // ed è iscritto

        await using var bob = await w.App.ConnectAsync(w.BobToken);
        Assert.True((await bob.Hub.InvokeAsync<HubWatchReply>("WatchFrom", w.Private, 0L)).NotFound);
    }

    [Fact]
    public async Task Typing_ReachesTheOthersWatching_NotTheSender_NotOutsiders()
    {
        using var w = await WorldAsync();
        (await w.Alice.PostAsJsonAsync($"/api/workspace/channels/{w.Private}/members", new { name = "bob@test.it" })).EnsureSuccessStatusCode();
        await using var alice = await w.App.ConnectAsync(w.AliceToken);
        await using var bob = await w.App.ConnectAsync(w.BobToken);
        await alice.WatchAsync(w.Private);
        await bob.WatchAsync(w.Private);

        await alice.Hub.SendAsync("Typing", w.Private, true);
        var seen = Assert.Single(await bob.CollectAsync(e => e.Kind == "typing", Window));
        Assert.Equal(("alice@test.it", true, true), (seen.Agent, seen.Typing, seen.Human));
        Assert.Empty(await alice.CollectAsync(e => e.Kind == "typing", Window));
    }

    [Fact]
    public async Task Typing_IntoAChannelYouCannotSee_IsIgnored()
    {
        using var w = await WorldAsync();
        await using var alice = await w.App.ConnectAsync(w.AliceToken);
        await using var bob = await w.App.ConnectAsync(w.BobToken);
        await alice.WatchAsync(w.Private);

        await bob.Hub.SendAsync("Typing", w.Private, true); // bob non è membro del canale privato
        Assert.Empty(await alice.CollectAsync(e => e.Kind == "typing", Window));
    }
}

// La migrazione ricava il creatore dei canali già esistenti dal messaggio "<utente> ha creato il canale".
public sealed class Phase8MigrationTests : IDisposable
{
    readonly string database = TestDb.NewName();

    [Fact]
    public async Task ExistingChannels_GetTheirCreatorFromTheSystemMessage()
    {
        var options = new DbContextOptionsBuilder<AppDb>().UseNpgsql(TestDb.ConnectionString(database)).Options;
        await using (var db = new AppDb(options))
        {
            await db.Database.EnsureDeletedAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261119023417_ApiTokens");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Channels" ("Name", "CreatedAt", "IsPrivate") VALUES ('vecchio', now(), false);
                INSERT INTO "Messages" ("ChannelId", "Author", "Kind", "Text", "At") VALUES (1, 'Sistema', 2, 'anna@test.it ha creato il canale privato.', now());
                """);
        }
        await using (var db = new AppDb(options))
        {
            await Database.MigrateAsync(db);
            Assert.Equal("anna@test.it", (await db.Channels.SingleAsync()).CreatedBy);
        }
    }

    public void Dispose() => TestDb.Drop(database);
}
