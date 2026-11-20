using System.Net;
using System.Net.Http.Json;
using AgentWeb.Shared.Workspace;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWeb.Tests;

// Mondo fisso per la matrice: alice possiede il canale privato P, bob il pubblico Q, carol è admin ma non membro di P.
public sealed class MatrixWorld : IAsyncLifetime
{
    public TestApp App { get; } = new();
    public Dictionary<string, HttpClient> Actors { get; } = [];
    public int P, Q, TaskInP;

    public async Task InitializeAsync()
    {
        await App.CreateUserAsync("alice@test.it");
        await App.CreateUserAsync("bob@test.it");
        await App.CreateUserAsync("carol@test.it", admin: true);
        Actors["anon"] = App.CreateClient();
        Actors["alice"] = await App.LoginAsync("alice@test.it");
        Actors["bob"] = await App.LoginAsync("bob@test.it");
        Actors["carol"] = await App.LoginAsync("carol@test.it");

        P = (await Read<ChannelDto>(await Actors["alice"].PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("segreto", true)))).Id;
        Q = (await Read<ChannelDto>(await Actors["bob"].PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("generale")))).Id;
        TaskInP = (await Read<TaskDto>(await Actors["alice"].PostAsJsonAsync($"/api/workspace/channels/{P}/tasks", new NewTaskRequest("piano", "", null)))).Id;
    }

    static async Task<T> Read<T>(HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    public Task DisposeAsync() { App.Dispose(); return Task.CompletedTask; }
}

// Fase 3 — dominio e API: matrice attore × rotta × risorsa eseguibile. Ogni riga è un'invariante.
public sealed class Phase3AuthorizationTests(MatrixWorld w) : IClassFixture<MatrixWorld>
{
    public static TheoryData<string, string, string, int> Matrix => new()
    {
        // Anonimo: niente API.
        { "anon", "GET", "/api/workspace/channels", 401 },
        { "anon", "GET", "/api/workspace/channels/{P}/messages", 401 },
        { "anon", "GET", "/api/settings", 401 },
        // Canale privato P: solo alice (membro). Per gli altri, admin compreso, non esiste (404).
        { "alice", "GET", "/api/workspace/channels/{P}/messages", 200 },
        { "bob", "GET", "/api/workspace/channels/{P}/messages", 404 },
        { "carol", "GET", "/api/workspace/channels/{P}/messages", 404 },
        { "alice", "POST", "/api/workspace/channels/{P}/messages", 204 },
        { "bob", "POST", "/api/workspace/channels/{P}/messages", 404 },
        { "carol", "POST", "/api/workspace/channels/{P}/messages", 404 },
        { "alice", "GET", "/api/workspace/channels/{P}/messages/since/0", 200 },
        { "bob", "GET", "/api/workspace/channels/{P}/messages/since/0", 404 },
        { "alice", "GET", "/api/workspace/channels/{P}/tasks", 200 },
        { "bob", "GET", "/api/workspace/channels/{P}/tasks", 404 },
        { "bob", "POST", "/api/workspace/channels/{P}/tasks", 404 },
        { "bob", "POST", "/api/workspace/tasks/{T}/status", 404 },
        { "carol", "POST", "/api/workspace/tasks/{T}/status", 404 },
        { "alice", "GET", "/api/workspace/channels/{P}/members", 200 },
        { "bob", "GET", "/api/workspace/channels/{P}/members", 404 },
        { "bob", "POST", "/api/workspace/channels/{P}/members", 404 },
        { "alice", "POST", "/api/workspace/channels/{P}/read", 204 },
        { "bob", "POST", "/api/workspace/channels/{P}/read", 404 },
        { "carol", "POST", "/api/workspace/channels/{P}/read", 404 },
        { "anon", "GET", "/api/workspace/search?q=piano", 401 },
        { "bob", "GET", "/api/workspace/search?q=piano", 200 },
        { "anon", "GET", "/api/workspace/activity", 401 },
        { "bob", "GET", "/api/workspace/activity", 200 },
        { "anon", "GET", "/api/daemon/machines", 401 },
        { "anon", "POST", "/api/daemon/poll", 401 },
        { "bob", "POST", "/api/daemon/deliveries/inesistente/complete", 404 },
        // Canale pubblico Q: tutti gli autenticati.
        { "alice", "GET", "/api/workspace/channels/{Q}/messages", 200 },
        { "carol", "POST", "/api/workspace/channels/{Q}/messages", 204 },
        { "alice", "GET", "/api/workspace/channels/{Q}/tasks", 200 },
        // Amministrazione: solo il ruolo Admin.
        { "alice", "GET", "/api/settings", 403 },
        { "carol", "GET", "/api/settings", 200 },
        { "bob", "GET", "/api/admin/users", 403 },
        { "carol", "GET", "/api/admin/users", 200 },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Actor_Route_Resource(string actor, string method, string route, int expected)
    {
        var url = route.Replace("{P}", w.P.ToString()).Replace("{Q}", w.Q.ToString()).Replace("{T}", w.TaskInP.ToString());
        var req = new HttpRequestMessage(new HttpMethod(method), url) { Content = method == "POST" ? JsonContent.Create(BodyFor(url)) : null };
        var r = await w.Actors[actor].SendAsync(req);
        Assert.Equal(expected, (int)r.StatusCode);
    }

    static object BodyFor(string url) => url switch
    {
        _ when url.EndsWith("/messages") => new NewMessageRequest("ciao"),
        _ when url.EndsWith("/tasks") => new NewTaskRequest("t", "", null),
        _ when url.EndsWith("/status") => new { status = WorkStatus.Todo },
        _ when url.EndsWith("/members") => new { name = "bob@test.it" },
        _ when url.EndsWith("/read") => new MarkReadRequest(0),
        _ => new { },
    };

    [Fact]
    public async Task ChannelList_HidesPrivateChannelsFromNonMembers()
    {
        var bob = await w.Actors["bob"].GetFromJsonAsync<List<ChannelDto>>("/api/workspace/channels");
        var carol = await w.Actors["carol"].GetFromJsonAsync<List<ChannelDto>>("/api/workspace/channels");
        var alice = await w.Actors["alice"].GetFromJsonAsync<List<ChannelDto>>("/api/workspace/channels");
        Assert.DoesNotContain(bob!, c => c.Id == w.P);
        Assert.DoesNotContain(carol!, c => c.Id == w.P);
        Assert.Contains(alice!, c => c.Id == w.P && c.IsPrivate);
        Assert.Contains(bob!, c => c.Id == w.Q);
    }

    // Endpoint generati dal framework a cui non si possono applicare convenzioni: solo contenuti statici pubblici.
    static readonly (string Prefix, string Why)[] FrameworkPublic =
    [
        ("_framework/resource-collection", "elenco degli asset statici generato da MapRazorComponents"),
        ("{**path:file}", "fallback dei file statici (wwwroot) in Development"),
    ];

    // Ogni endpoint deve dichiarare una policy (Authorize o AllowAnonymous): una rotta nuova "dimenticata" fa fallire il test.
    [Fact]
    public void EveryEndpoint_DeclaresAnAuthorizationPolicy()
    {
        var missing = w.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(e => !FrameworkPublic.Any(f => ((e as RouteEndpoint)?.RoutePattern.RawText ?? "").StartsWith(f.Prefix)))
            .Select(e => $"{e.DisplayName ?? "?"} [{(e as RouteEndpoint)?.RoutePattern.RawText}] " +
                string.Join(",", e.Metadata.Select(m => m.GetType().Name).Distinct().Take(8)))
            .ToList();
        Assert.True(missing.Count == 0, "Endpoint senza policy:\n" + string.Join("\n", missing));
    }
}

public sealed class Phase3IdempotencyTests
{
    [Fact]
    public async Task SameClientId_PostedTwice_IsStoredOnce()
    {
        using var app = new TestApp();
        await app.CreateUserAsync("alice@test.it");
        using var http = await app.LoginAsync("alice@test.it");
        var c = (await (await http.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("idem"))).Content.ReadFromJsonAsync<ChannelDto>())!;

        var body = new NewMessageRequest("una volta sola", "retry-123");
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsJsonAsync($"/api/workspace/channels/{c.Id}/messages", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsJsonAsync($"/api/workspace/channels/{c.Id}/messages", body)).StatusCode);

        var messages = await http.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{c.Id}/messages");
        Assert.Single(messages!, m => m.Text == "una volta sola");
    }
}

public sealed class Phase13OpsTests
{
    [Fact]
    public async Task OpenApi_IsDeterministic_AndNotPublic()
    {
        using var app = new TestApp();
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/openapi/v1.json")).StatusCode);
        var first = await alice.GetStringAsync("/openapi/v1.json");
        Assert.Equal(first, await alice.GetStringAsync("/openapi/v1.json"));
        Assert.Contains("/api/workspace/channels", first);
        Assert.Contains("/api/daemon/poll", first);

        using var doc = System.Text.Json.JsonDocument.Parse(first);
        Assert.StartsWith("3.2", doc.RootElement.GetProperty("openapi").GetString()); // default di ASP.NET Core 11
        Assert.Equal("bearer", doc.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("scheme").GetString());
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/workspace/channels").GetProperty("get").TryGetProperty("security", out _));
        Assert.False(paths.GetProperty("/healthz").GetProperty("get").TryGetProperty("security", out _)); // pubblico
    }

    [Fact]
    public async Task Healthz_AnswersWithoutLogin_WhenTheDatabaseIsUp()
    {
        using var app = new TestApp();
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/healthz")).StatusCode);
    }

    // Chi accetta zstd (app MAUI, CLI, browser recenti) lo riceve; gli altri restano su br/gzip.
    [Fact]
    public async Task Responses_AreZstdCompressed_WhenTheClientAcceptsIt()
    {
        using var app = new TestApp();
        var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        req.Headers.AcceptEncoding.ParseAdd("zstd, br, gzip");
        var res = await app.CreateClient().SendAsync(req);
        Assert.Equal("zstd", Assert.Single(res.Content.Headers.ContentEncoding));
        await using var body = new System.IO.Compression.ZstandardStream(await res.Content.ReadAsStreamAsync(), System.IO.Compression.CompressionMode.Decompress);
        Assert.Equal("\"ok\"", await new StreamReader(body).ReadToEndAsync());
    }
}
