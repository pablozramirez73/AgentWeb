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
using Microsoft.Extensions.DependencyInjection;

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
    public async Task MessageAsTask_AgentConfirmsWorksAndDeliversInTheThread_AndResumesOnReply()
    {
        // Senza agenti nel canale il task resta da assegnare.
        var free = (await (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/tasks/from-text", new { text = "Prepara la scaletta" })).Content.ReadFromJsonAsync<TaskDto>())!;
        Assert.Null(free.Assignee);
        Assert.NotNull(free.MessageId);
        (await alice.PostAsJsonAsync($"/api/workspace/tasks/{free.Id}/status", new { status = WorkStatus.Closed })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await AddAgent(alice, "Esperto")).StatusCode);
        var request = (await (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/tasks/from-text", new { text = "Mappa i moduli del progetto" })).Content.ReadFromJsonAsync<TaskDto>())!;
        Assert.Equal(("Esperto", true, "Esperto", "Mappa i moduli del progetto"), (request.Assignee, request.AssigneeIsAgent, request.Agent, request.Title));

        var thread = await WaitForAsync(m => m.Count(x => x.ParentId == request.MessageId && x.Author == "Esperto") >= 2);
        var replies = thread.Where(x => x.ParentId == request.MessageId).Select(x => x.Text).ToList();
        Assert.StartsWith($"Lo prendo io come task #{request.Id}", replies[0]);
        Assert.Equal("risposta dell'esperto", replies[1]); // il risultato sta nel thread della richiesta
        for (var i = 0; i < 50 && (await Task1Of(request.Id)).Status != WorkStatus.Done; i++) await Task.Delay(100);
        Assert.Equal(WorkStatus.Done, (await Task1Of(request.Id)).Status);

        // Un messaggio ha un solo task; una correzione nel thread rimette l'agente al lavoro.
        Assert.Equal(request.Id, (await (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages/{request.MessageId}/task", new { })).Content.ReadFromJsonAsync<TaskDto>())!.Id);
        (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages", new NewMessageRequest("aggiungi anche le dipendenze", ParentId: request.MessageId))).EnsureSuccessStatusCode();
        await WaitForAsync(m => m.Count(x => x.ParentId == request.MessageId && x.Text == "risposta dell'esperto") >= 2);
        Assert.Equal(WorkStatus.Done, (await Task1Of(request.Id)).Status);
    }

    async Task<TaskDto> Task1Of(int id) => (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{channel}/tasks"))!.Single(t => t.Id == id);

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

// L'agente che ha bisogno di una decisione passa il task a chi l'ha chiesto; la risposta nel thread glielo restituisce.
public sealed class TaskHandBackTests
{
    [Fact]
    public async Task AgentHandsTaskToRequester_AndAnswerInThreadGivesItBack()
    {
        await using var llm = await FakeLlm.StartAsync("PER TE: preferisci PDF o Markdown?");
        using var app = new TestApp();
        var settings = new AgentSettings { LocalAgents = [new() { Name = "Cindy", Prompt = "Ricerche.", Llm = new() { Kind = LlmKinds.OpenAI, Url = llm.Url + "/v1", ApiKey = "k", DefaultModel = "m" } }] };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(settings));
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        var channel = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("ricerca"))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/agents", new { name = "Cindy" })).EnsureSuccessStatusCode();

        var task = (await (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/tasks/from-text", new { text = "Riassumi le trimestrali" })).Content.ReadFromJsonAsync<TaskDto>())!;
        async Task<TaskDto> Current() => (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{channel}/tasks"))!.Single();
        for (var i = 0; i < 100 && (await Current()).Assignee != "alice@test.it"; i++) await Task.Delay(100);
        var handed = await Current();
        Assert.Equal(("alice@test.it", false, WorkStatus.Todo, "Cindy"), (handed.Assignee, handed.AssigneeIsAgent, handed.Status, handed.Agent));
        var messages = (await alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{channel}/messages"))!;
        Assert.Contains(messages, m => m.ParentId == task.MessageId && m.Text.Contains("passa a te") && m.Text.Contains("PDF o Markdown"));

        (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages", new NewMessageRequest("Markdown", ParentId: task.MessageId))).EnsureSuccessStatusCode();
        for (var i = 0; i < 100 && llm.Requests.Count < 2; i++) await Task.Delay(100);
        Assert.True(llm.Requests.Count >= 2); // l'agente è ripartito con la risposta
    }

    // Doppio clic (o clic e agente insieme) su "Crea task dal messaggio": un solo task, e tutti ricevono quello.
    [Fact]
    public async Task ConcurrentTaskFromMessage_CreatesOneTask()
    {
        using var app = new TestApp();
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        var channel = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("gara"))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages", new NewMessageRequest("prepara il report"))).EnsureSuccessStatusCode();
        var msg = (await alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{channel}/messages"))!.Single(m => m.Kind == AuthorKind.User);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            var r = await alice.PostAsync($"/api/workspace/channels/{channel}/messages/{msg.Id}/task", null);
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.IsSuccessStatusCode && body.Length > 0, $"{(int)r.StatusCode}: {body}");
            return System.Text.Json.JsonSerializer.Deserialize<TaskDto>(body, System.Text.Json.JsonSerializerOptions.Web)!.Id;
        }));
        var t = Assert.Single((await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{channel}/tasks"))!);
        Assert.All(results, id => Assert.Equal(t.Id, id));
    }

    // Un "grazie" nel thread di un task finito non lo riapre: il risultato resta, l'agente non rigira.
    [Fact]
    public async Task ReplyToADoneTask_KeepsItDone_AndDoesNotRerun()
    {
        await using var llm = await FakeLlm.StartAsync("Ecco il riassunto.");
        using var app = new TestApp();
        var settings = new AgentSettings { LocalAgents = [new() { Name = "Cindy", Prompt = "Ricerche.", Llm = new() { Kind = LlmKinds.OpenAI, Url = llm.Url + "/v1", ApiKey = "k", DefaultModel = "m" } }] };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(settings));
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        var channel = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("ricerca"))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/agents", new { name = "Cindy" })).EnsureSuccessStatusCode();
        var task = (await (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/tasks/from-text", new { text = "Riassumi le trimestrali" })).Content.ReadFromJsonAsync<TaskDto>())!;
        async Task<TaskDto> Current() => (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{channel}/tasks"))!.Single();
        for (var i = 0; i < 100 && (await Current()).Status != WorkStatus.Done; i++) await Task.Delay(100);
        int Calls() { lock (llm.Requests) return llm.Requests.Count; }
        var calls = Calls();

        (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages", new NewMessageRequest("grazie", ParentId: task.MessageId))).EnsureSuccessStatusCode();
        await Task.Delay(1500);
        var t = await Current();
        Assert.Equal((WorkStatus.Done, "Ecco il riassunto."), (t.Status, t.Result));
        Assert.Equal(calls, Calls());
    }

    // Task delegato da un agente: non c'è una persona a cui passarlo, quindi fallisce con la nota (niente "Da fare" mai in coda).
    [Fact]
    public async Task AgentHandsBack_ToARequestingAgent_FailsWithTheNote()
    {
        await using var llm = await FakeLlm.StartAsync("PER TE: preferisci PDF o Markdown?");
        using var app = new TestApp();
        LocalAgentConfig Agent(string name) => new() { Name = name, Prompt = "Ricerche.", Llm = new() { Kind = LlmKinds.OpenAI, Url = llm.Url + "/v1", ApiKey = "k", DefaultModel = "m" } };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(new AgentSettings { LocalAgents = [Agent("Bob"), Agent("Cindy")] }));
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        var channel = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("ricerca"))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        foreach (var a in new[] { "Bob", "Cindy" }) (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/agents", new { name = a })).EnsureSuccessStatusCode();

        var store = app.Services.GetRequiredService<WorkspaceStore>();
        var task = (await store.CreateTaskByAgentAsync(channel, "Bob", "Riassumi le trimestrali", "", "Cindy", 5))!;
        async Task<TaskDto> Current() => (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{channel}/tasks"))!.Single(t => t.Id == task.Id);
        for (var i = 0; i < 100 && (await Current()).Status != WorkStatus.Failed; i++) await Task.Delay(100);
        var t = await Current();
        Assert.Equal((WorkStatus.Failed, "Cindy"), (t.Status, t.Assignee));
        var messages = (await alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{channel}/messages"))!;
        Assert.DoesNotContain(messages, m => m.Text.Contains("passa a te"));
        Assert.Contains(messages, m => m.Author == "Cindy" && m.Text.Contains("PDF o Markdown"));
    }

    [Fact]
    public async Task MentionedAgent_TakesRealWorkAsTask()
    {
        await using var llm = await FakeLlm.StartAsync("TASK: preparo la mappa dei moduli");
        using var app = new TestApp();
        var settings = new AgentSettings { LocalAgents = [new() { Name = "Cindy", Prompt = "Ricerche.", Llm = new() { Kind = LlmKinds.OpenAI, Url = llm.Url + "/v1", ApiKey = "k", DefaultModel = "m" } }] };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(settings));
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        var channel = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("codice"))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/agents", new { name = "Cindy" })).EnsureSuccessStatusCode();
        (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages", new NewMessageRequest("@Cindy mappa i moduli del progetto"))).EnsureSuccessStatusCode();

        List<TaskDto> tasks = [];
        for (var i = 0; i < 100 && tasks.Count == 0; i++) { await Task.Delay(100); tasks = (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{channel}/tasks"))!; }
        var t = Assert.Single(tasks);
        Assert.Equal(("Cindy", "alice@test.it"), (t.Agent, t.CreatedBy)); // la richiesta resta di chi l'ha fatta
        var messages = (await alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{channel}/messages"))!;
        Assert.Contains(messages, m => m.ParentId == t.MessageId && m.Text == $"Lo prendo io come task #{t.Id}.\npreparo la mappa dei moduli");
    }

    // DM con l'agente: rispondere nel thread di un suo task riprende il task, e basta (niente risposta di chat in più).
    [Fact]
    public async Task DirectMessage_ReplyInTaskThread_RunsTheAgentOnce()
    {
        await using var llm = await FakeLlm.StartAsync("Fatto.");
        using var app = new TestApp();
        var settings = new AgentSettings { LocalAgents = [new() { Name = "Cindy", Prompt = "Ricerche.", Llm = new() { Kind = LlmKinds.OpenAI, Url = llm.Url + "/v1", ApiKey = "k", DefaultModel = "m" } }] };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(settings));
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        var dm = (await (await alice.PostAsJsonAsync("/api/workspace/direct", new { name = "Cindy" })).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        var t = (await (await alice.PostAsJsonAsync($"/api/workspace/channels/{dm}/tasks/from-text", new { text = "scrivi il report" })).Content.ReadFromJsonAsync<TaskDto>())!;
        Assert.Equal("Cindy", t.Agent);

        int Calls() { lock (llm.Requests) return llm.Requests.Count; }
        async Task<int> SettledAsync(int atLeast)
        {
            for (var i = 0; i < 100 && Calls() < atLeast; i++) await Task.Delay(100);
            await Task.Delay(1500); // eventuali chiamate in più arrivano entro questo margine
            return Calls();
        }
        var afterTask = await SettledAsync(1);
        (await alice.PostAsJsonAsync($"/api/workspace/channels/{dm}/messages", new NewMessageRequest("aggiungi i grafici", ParentId: t.MessageId))).EnsureSuccessStatusCode();
        Assert.Equal(afterTask + 1, await SettledAsync(afterTask + 1));
    }

    // In un messaggio diretto con l'agente non c'è la @menzione: la richiesta è il messaggio che l'ha attivato.
    [Fact]
    public async Task DirectMessage_RealWork_BecomesATask_NotARawMarker()
    {
        await using var llm = await FakeLlm.StartAsync("TASK: preparo la mappa dei moduli");
        using var app = new TestApp();
        var settings = new AgentSettings { LocalAgents = [new() { Name = "Cindy", Prompt = "Ricerche.", Llm = new() { Kind = LlmKinds.OpenAI, Url = llm.Url + "/v1", ApiKey = "k", DefaultModel = "m" } }] };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(settings));
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        var dm = (await (await alice.PostAsJsonAsync("/api/workspace/direct", new { name = "Cindy" })).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        (await alice.PostAsJsonAsync($"/api/workspace/channels/{dm}/messages", new NewMessageRequest("mappa i moduli del progetto"))).EnsureSuccessStatusCode();

        List<TaskDto> tasks = [];
        for (var i = 0; i < 100 && tasks.Count == 0; i++) { await Task.Delay(100); tasks = (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{dm}/tasks"))!; }
        var t = Assert.Single(tasks);
        Assert.Equal(("Cindy", "alice@test.it"), (t.Agent, t.CreatedBy));
        var messages = (await alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{dm}/messages"))!;
        Assert.Equal($"Lo prendo io come task #{t.Id}.\npreparo la mappa dei moduli", messages.First(m => m.Author == "Cindy").Text); // non "TASK: …"
    }
}
