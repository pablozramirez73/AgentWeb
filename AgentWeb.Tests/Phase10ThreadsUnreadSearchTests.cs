using System.Net;
using System.Net.Http.Json;
using AgentWeb.Shared.Workspace;

namespace AgentWeb.Tests;

// Fase 10 — thread, non letti e ricerca: stesse regole di visibilità di tutto il resto (ChannelVisibility).
public sealed class Phase10ThreadsUnreadSearchTests : IAsyncLifetime
{
    readonly TestApp app = new();
    HttpClient alice = null!, bob = null!;

    public async Task InitializeAsync()
    {
        await app.CreateUserAsync("alice@test.it");
        await app.CreateUserAsync("bob@test.it");
        alice = await app.LoginAsync("alice@test.it");
        bob = await app.LoginAsync("bob@test.it");
    }

    public Task DisposeAsync() { app.Dispose(); return Task.CompletedTask; }

    async Task<int> ChannelAsync(HttpClient http, string name, bool isPrivate = false) =>
        (await (await http.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest(name, isPrivate))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;

    static async Task<MessageDto> PostAsync(HttpClient http, int channel, string text, long? parent = null)
    {
        (await http.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages", new NewMessageRequest(text, null, parent))).EnsureSuccessStatusCode();
        return (await http.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{channel}/messages"))!.Last(m => m.Text == text);
    }

    static async Task<ChannelDto> ListedAsync(HttpClient http, int channel) =>
        (await http.GetFromJsonAsync<List<ChannelDto>>("/api/workspace/channels"))!.Single(c => c.Id == channel);

    [Fact]
    public async Task Replies_GoIntoTheThreadOfTheRoot_OneLevelOnly()
    {
        var c = await ChannelAsync(alice, "thread");
        var root = await PostAsync(alice, c, "proposta");
        var reply = await PostAsync(bob, c, "d'accordo", root.Id);
        var nested = await PostAsync(alice, c, "grazie", reply.Id); // risposta a una risposta: stesso thread

        Assert.Equal(root.Id, reply.ParentId);
        Assert.Equal(root.Id, nested.ParentId);
        var thread = await alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{c}/messages/{root.Id}/thread");
        Assert.Equal(["proposta", "d'accordo", "grazie"], thread!.Select(m => m.Text));
    }

    [Fact]
    public async Task Reply_ToAMessageOfAnotherChannel_IsRejected()
    {
        var a = await ChannelAsync(alice, "uno");
        var b = await ChannelAsync(alice, "due");
        var root = await PostAsync(alice, a, "qui");
        var r = await alice.PostAsJsonAsync($"/api/workspace/channels/{b}/messages", new NewMessageRequest("altrove", null, root.Id));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Thread_OfAPrivateChannel_DoesNotExistForOutsiders()
    {
        var p = await ChannelAsync(alice, "riservato", isPrivate: true);
        var root = await PostAsync(alice, p, "segreto");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/workspace/channels/{p}/messages/{root.Id}/thread")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/workspace/channels/{p}/messages", new NewMessageRequest("dentro?", null, root.Id))).StatusCode);
    }

    [Fact]
    public async Task Unread_CountsOthersMessages_Mentions_AndMarkReadClearsThem()
    {
        var c = await ChannelAsync(alice, "novita");
        await PostAsync(alice, c, "primo");
        await PostAsync(alice, c, "ehi @bob guarda qui");
        var last = await PostAsync(alice, c, "terzo");

        var forBob = await ListedAsync(bob, c);
        Assert.Equal(3, forBob.Unread);
        Assert.Equal(1, forBob.Mentions);
        Assert.Equal(0, (await ListedAsync(alice, c)).Unread); // i propri messaggi non contano

        (await bob.PostAsJsonAsync($"/api/workspace/channels/{c}/read", new MarkReadRequest(last.Id))).EnsureSuccessStatusCode();
        // Un client in ritardo che manda un cursore vecchio non fa ricomparire i non letti.
        (await bob.PostAsJsonAsync($"/api/workspace/channels/{c}/read", new MarkReadRequest(0))).EnsureSuccessStatusCode();
        Assert.Equal((0, 0), ((await ListedAsync(bob, c)).Unread, (await ListedAsync(bob, c)).Mentions));

        await PostAsync(alice, c, "quarto");
        Assert.Equal(1, (await ListedAsync(bob, c)).Unread);
    }

    [Fact]
    public async Task MentionCount_TreatsUnderscoreAndPercentLiterally()
    {
        await app.CreateUserAsync("a_b@test.it");
        var ab = await app.LoginAsync("a_b@test.it");
        var c = await ChannelAsync(alice, "jolly");
        await PostAsync(alice, c, "@axb non sei tu");
        await PostAsync(alice, c, "@a_b sì");
        Assert.Equal(1, (await ListedAsync(ab, c)).Mentions);
    }

    [Fact]
    public async Task Search_FindsWords_OnlyInChannelsYouCanSee()
    {
        var pub = await ChannelAsync(alice, "pubblico");
        var priv = await ChannelAsync(alice, "privato", isPrivate: true);
        await PostAsync(alice, pub, "il deploy di venerdì è confermato");
        await PostAsync(alice, priv, "il deploy segreto");

        var forAlice = await alice.GetFromJsonAsync<List<SearchHitDto>>("/api/workspace/search?q=deploy");
        var forBob = await bob.GetFromJsonAsync<List<SearchHitDto>>("/api/workspace/search?q=deploy");
        Assert.Equal(2, forAlice!.Count);
        Assert.Equal(["pubblico"], forBob!.Select(h => h.ChannelName));
        Assert.Empty((await bob.GetFromJsonAsync<List<SearchHitDto>>("/api/workspace/search?q=segreto"))!);
    }

    [Fact]
    public async Task Activity_ReachesWhoCanSeeTheChannel_EvenWhenNotWatchingIt()
    {
        var pub = await ChannelAsync(alice, "piazza");
        var priv = await ChannelAsync(alice, "stanza", isPrivate: true);
        await using var bobLive = await app.ConnectAsync((await app.LoginTokensAsync("bob@test.it")).AccessToken);

        await PostAsync(alice, priv, "solo per alice");
        await PostAsync(alice, pub, "per tutti");

        var got = await bobLive.CollectAsync(e => e.Kind is "message" or "activity", TimeSpan.FromSeconds(3));
        Assert.Equal(["message:per tutti", "activity:per tutti"], got.Select(e => $"{e.Kind}:{e.Message!.Text}"));
        Assert.All(got, e => Assert.Equal(pub, e.ChannelId)); // anche la copia per le app vecchie rispetta la visibilità

        // Chi guarda il canale riceve ogni messaggio una volta sola.
        Assert.True(await bobLive.WatchAsync(pub));
        await PostAsync(alice, pub, "una volta");
        Assert.Single(await bobLive.CollectAsync(e => e.Kind == "message" && e.Message!.Text == "una volta", TimeSpan.FromSeconds(2)));
    }

    // Pagina Attività: canali visibili + thread che mi riguardano; leggere un thread non segna letto il resto del canale.
    [Fact]
    public async Task Feed_ListsMyThreadsAndVisibleChannels_WithSeparateUnread()
    {
        var c = await ChannelAsync(alice, "inbox");
        var secret = await ChannelAsync(alice, "segreto", isPrivate: true);
        await PostAsync(alice, secret, "solo per alice");
        var mine = await PostAsync(bob, c, "domanda di bob");
        await PostAsync(alice, c, "risposta di alice", mine.Id);
        var other = await PostAsync(alice, c, "@bob guarda qui");
        await PostAsync(alice, c, "dettaglio", other.Id);
        var unrelated = await PostAsync(alice, c, "thread non di bob");
        await PostAsync(alice, c, "seguito", unrelated.Id);
        await PostAsync(alice, c, "novità nel canale");

        var feed = (await bob.GetFromJsonAsync<List<FeedItemDto>>("/api/workspace/feed"))!;
        Assert.DoesNotContain(feed, f => f.ChannelId == secret);
        var channel = Assert.Single(feed, f => f.Root is null && f.ChannelId == c);
        Assert.Equal(("novità nel canale", 3), (channel.Last.Text, channel.Unread)); // i 3 messaggi di alice; le risposte nei thread non contano qui
        var threads = feed.Where(f => f.Root is not null).ToList();
        Assert.Equal(["@bob guarda qui", "domanda di bob"], threads.Select(t => t.Root!.Text)); // non quello che non lo riguarda
        Assert.All(threads, t => Assert.Equal((1, 1), (t.Replies, t.Unread)));
        Assert.Equal(feed.Select(f => f.Last.Id).OrderDescending(), feed.Select(f => f.Last.Id));

        var t = threads[0];
        (await bob.PostAsJsonAsync($"/api/workspace/channels/{c}/threads/{t.Root!.Id}/read", new MarkReadRequest(t.Last.Id))).EnsureSuccessStatusCode();
        feed = (await bob.GetFromJsonAsync<List<FeedItemDto>>("/api/workspace/feed"))!;
        Assert.Equal(0, feed.Single(f => f.Root?.Id == t.Root.Id).Unread);
        Assert.Equal(3, feed.Single(f => f.Root is null && f.ChannelId == c).Unread);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/workspace/channels/{secret}/threads/{t.Root.Id}/read", new MarkReadRequest(1))).StatusCode);
    }
}
