using System.Net.Http.Json;
using AgentWeb.Shared.Workspace;

namespace AgentWeb.Tests;

// Fase 4 — realtime: consegna una volta sola e solo a chi vede il canale, ordine monotono, replay, revoca che chiude la connessione.
public sealed class Phase4RealtimeTests
{
    static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    sealed record World(TestApp App, HttpClient Alice, HttpClient Bob, string AliceToken, string BobToken, int P, int Q) : IDisposable
    {
        public void Dispose() { Alice.Dispose(); Bob.Dispose(); App.Dispose(); }
    }

    static async Task<World> WorldAsync(Dictionary<string, string?>? settings = null)
    {
        var app = new TestApp(settings);
        await app.CreateUserAsync("alice@test.it");
        await app.CreateUserAsync("bob@test.it");
        var aliceToken = (await app.LoginTokensAsync("alice@test.it")).AccessToken;
        var bobToken = (await app.LoginTokensAsync("bob@test.it")).AccessToken;
        var alice = app.Bearer(aliceToken);
        var bob = app.Bearer(bobToken);
        var p = await CreateChannelAsync(alice, "segreto", true);
        var q = await CreateChannelAsync(bob, "generale", false);
        return new(app, alice, bob, aliceToken, bobToken, p, q);
    }

    static async Task<int> CreateChannelAsync(HttpClient http, string name, bool isPrivate)
    {
        var r = await http.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest(name, isPrivate));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ChannelDto>())!.Id;
    }

    static Task PostAsync(HttpClient http, int channel, string text) =>
        http.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages", new NewMessageRequest(text)).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    [Fact]
    public async Task Watch_UsesTheSameVisibilityRuleAsHttp()
    {
        using var w = await WorldAsync();
        await using var alice = await w.App.ConnectAsync(w.AliceToken);
        await using var bob = await w.App.ConnectAsync(w.BobToken);
        Assert.True(await alice.WatchAsync(w.P));
        Assert.False(await bob.WatchAsync(w.P));
        Assert.True(await bob.WatchAsync(w.Q));
    }

    [Fact]
    public async Task Message_ArrivesExactlyOnce_AndOnlyToWhoCanSeeIt()
    {
        using var w = await WorldAsync();
        await using var alice = await w.App.ConnectAsync(w.AliceToken);
        await using var bob = await w.App.ConnectAsync(w.BobToken);
        await alice.WatchAsync(w.P);
        await bob.WatchAsync(w.P); // rifiutato: bob prova comunque a iscriversi al privato
        await PostAsync(w.Alice, w.P, "solo per alice");

        var toAlice = await alice.CollectAsync(e => e.Kind == "message" && e.Message?.Text == "solo per alice", Window);
        var toBob = await bob.CollectAsync(e => e.ChannelId == w.P, Window); // nessun tipo di evento, nemmeno "activity"
        Assert.Single(toAlice);
        Assert.Empty(toBob);
    }

    // L'app nativa usa MessagePack (binario): stessi eventi, stessi campi, stessa consegna unica.
    [Fact]
    public async Task MessagePackClients_GetTheSameEvents()
    {
        using var w = await WorldAsync();
        await using var alice = await w.App.ConnectAsync(w.AliceToken, messagePack: true);
        Assert.True(await alice.WatchAsync(w.P));
        await PostAsync(w.Alice, w.P, "in binario");

        var got = Assert.Single(await alice.CollectAsync(e => e.Kind == "message" && e.Message?.Text == "in binario", Window));
        Assert.Equal(("alice@test.it", AuthorKind.User, w.P), (got.Message!.Author, got.Message.Kind, got.Message.ChannelId));
        Assert.True(got.Message.At > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task PrivateChannelCreation_IsNotBroadcast_PublicOneIs()
    {
        using var w = await WorldAsync();
        await using var alice = await w.App.ConnectAsync(w.AliceToken);
        await using var bob = await w.App.ConnectAsync(w.BobToken);

        var hidden = await CreateChannelAsync(w.Alice, "riservato", true);
        var open = await CreateChannelAsync(w.Alice, "aperto", false);

        var bobSaw = await bob.CollectAsync(e => e.Kind == "channel", Window);
        var aliceSaw = await alice.CollectAsync(e => e.Kind == "channel", Window);
        Assert.DoesNotContain(bobSaw, e => e.ChannelId == hidden);
        Assert.Contains(bobSaw, e => e.ChannelId == open);
        Assert.Contains(aliceSaw, e => e.ChannelId == hidden);
    }

    [Fact]
    public async Task AddedMember_SeesTheChannelAppear_AndCanWatchIt()
    {
        using var w = await WorldAsync();
        await using var bob = await w.App.ConnectAsync(w.BobToken);
        (await w.Alice.PostAsJsonAsync($"/api/workspace/channels/{w.P}/members", new { name = "bob@test.it" })).EnsureSuccessStatusCode();

        Assert.Single(await bob.CollectAsync(e => e.Kind == "channel" && e.ChannelId == w.P, Window, stopAfter: 1));
        Assert.True(await bob.WatchAsync(w.P));
    }

    [Fact]
    public async Task EventsForAChannel_ArriveInIdOrder_EvenWithConcurrentWriters()
    {
        using var w = await WorldAsync();
        (await w.Alice.PostAsJsonAsync($"/api/workspace/channels/{w.P}/members", new { name = "bob@test.it" })).EnsureSuccessStatusCode();
        await using var alice = await w.App.ConnectAsync(w.AliceToken);
        await alice.WatchAsync(w.P);

        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => PostAsync(i % 2 == 0 ? w.Alice : w.Bob, w.P, $"m{i}")));

        var ids = (await alice.CollectAsync(e => e.Kind == "message" && e.Message?.Text.StartsWith('m') == true, TimeSpan.FromSeconds(5), stopAfter: 30))
            .Select(e => e.Message!.Id).ToList();
        Assert.Equal(30, ids.Count);
        Assert.Equal(ids.Order(), ids);
        Assert.Equal(30, ids.Distinct().Count());
    }

    [Fact]
    public async Task Replay_ReturnsWhatWasMissed_AndResetsBeyondTheHorizon()
    {
        using var w = await WorldAsync(new() { ["Workspace:ReplayHorizon"] = "5" });
        var before = (await w.Alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{w.P}/messages"))!.Last().Id;

        for (var i = 0; i < 3; i++) await PostAsync(w.Alice, w.P, $"perso{i}");
        var replay = (await w.Alice.GetFromJsonAsync<ReplayDto>($"/api/workspace/channels/{w.P}/messages/since/{before}"))!;
        Assert.False(replay.Reset);
        Assert.Equal(["perso0", "perso1", "perso2"], replay.Messages.Select(m => m.Text));

        for (var i = 0; i < 5; i++) await PostAsync(w.Alice, w.P, $"altro{i}");
        var tooFar = (await w.Alice.GetFromJsonAsync<ReplayDto>($"/api/workspace/channels/{w.P}/messages/since/{before}"))!;
        Assert.True(tooFar.Reset);
        Assert.Empty(tooFar.Messages);
    }

    [Fact]
    public async Task RevokingTheSession_ClosesTheOpenConnection()
    {
        using var w = await WorldAsync();
        await using var alice = await w.App.ConnectAsync(w.AliceToken);
        await alice.WatchAsync(w.P);

        (await w.Alice.PostAsync("/api/account/sessions/revoke-all", null)).EnsureSuccessStatusCode();

        var closed = await Task.WhenAny(alice.Closed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(alice.Closed.Task, closed);
        await Assert.ThrowsAnyAsync<Exception>(() => w.App.ConnectAsync(w.AliceToken)); // e non si riconnette
    }
}

// Due repliche dietro lo stesso DB e lo stesso Redis: un messaggio scritto su A arriva una volta sola al client connesso a B.
// Richiede Redis: AGENTWEB_TEST_REDIS=localhost:6379 (es. docker run -p 6379:6379 redis).
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTWEB_TEST_REDIS") is null) Skip = "Imposta AGENTWEB_TEST_REDIS per il test multi-replica.";
    }
}

public sealed class Phase4MultiReplicaTests
{
    [RedisFact]
    public async Task MessageWrittenOnReplicaA_ReachesClientOnReplicaB_Once()
    {
        var redis = Environment.GetEnvironmentVariable("AGENTWEB_TEST_REDIS")!;
        using var a = new TestApp(new() { ["Redis"] = redis });
        using var b = new TestApp(new() { ["Redis"] = redis }, sharedDatabase: a.Database);
        await a.CreateUserAsync("alice@test.it");
        using var aliceOnA = await a.LoginAsync("alice@test.it");
        var tokenOnB = (await b.LoginTokensAsync("alice@test.it")).AccessToken;
        var r = await aliceOnA.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("replicato"));
        var channel = (await r.Content.ReadFromJsonAsync<ChannelDto>())!.Id;

        await using var live = await b.ConnectAsync(tokenOnB);
        Assert.True(await live.WatchAsync(channel));
        (await aliceOnA.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages", new NewMessageRequest("da A a B"))).EnsureSuccessStatusCode();

        var got = await live.CollectAsync(e => e.Kind == "message" && e.Message?.Text == "da A a B", TimeSpan.FromSeconds(3));
        Assert.Single(got);
    }
}
