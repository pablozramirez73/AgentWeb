using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Workspace;
using AgentWeb.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWeb.Tests;

// Parallelismo degli agenti: tempi misurati con un LLM finto che impiega 1,5 s per risposta.
public sealed class Phase7ParallelTests
{
    static async Task<(TestApp App, FakeLlm Llm, HttpClient Alice, int Channel)> WorldAsync(int agents, Dictionary<string, string?> settings)
    {
        var llm = await FakeLlm.StartAsync("fatto");
        var app = new TestApp(settings);
        var s = new AgentSettings
        {
            LocalAgents = Enumerable.Range(1, agents).Select(i => new LocalAgentConfig
            {
                Name = $"L{i}", Prompt = "Rispondi.", Llm = new() { Kind = LlmKinds.OpenAI, Url = llm.Url + "/v1", ApiKey = "k", DefaultModel = "lento" },
            }).ToList(),
        };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(s));
        await app.CreateUserAsync("alice@test.it");
        var alice = await app.LoginAsync("alice@test.it");
        var c = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("corsa"))).Content.ReadFromJsonAsync<ChannelDto>())!;
        for (var i = 1; i <= agents; i++)
            (await alice.PostAsJsonAsync($"/api/workspace/channels/{c.Id}/agents", new { name = $"L{i}" })).EnsureSuccessStatusCode();
        return (app, llm, alice, c.Id);
    }

    static async Task<TimeSpan> MentionAllAndWaitAsync(HttpClient alice, int channel, int agents)
    {
        var mentions = string.Join(" ", Enumerable.Range(1, agents).Select(i => $"@L{i}"));
        var sw = Stopwatch.StartNew();
        (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/messages", new NewMessageRequest(mentions + " via!"))).EnsureSuccessStatusCode();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            var replies = (await alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{channel}/messages"))!.Count(m => m.Kind == AuthorKind.Agent);
            if (replies == agents) return sw.Elapsed;
            await Task.Delay(50);
        }
        throw new TimeoutException("Gli agenti non hanno risposto entro 30 s.");
    }

    [Fact]
    public async Task MentionedAgents_WorkAtTheSameTime()
    {
        var (app, llm, alice, channel) = await WorldAsync(4, new() { ["Agents:MaxParallel"] = "4" });
        await using var _ = llm;
        using var __ = app;
        var elapsed = await MentionAllAndWaitAsync(alice, channel, 4);
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"4 agenti da 1,5 s in {elapsed.TotalSeconds:F1} s: uno dopo l'altro ne servirebbero 6");
    }

    [Fact]
    public async Task PerLlmLimit_KeepsAServerFromBeingFlooded()
    {
        var (app, llm, alice, channel) = await WorldAsync(3, new() { ["Agents:MaxParallel"] = "4", ["Agents:MaxParallelPerLlm"] = "1" });
        await using var _ = llm;
        using var __ = app;
        var elapsed = await MentionAllAndWaitAsync(alice, channel, 3);
        Assert.True(elapsed >= TimeSpan.FromSeconds(4.4), $"con 1 chiamata alla volta per server, 3 × 1,5 s non possono finire in {elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task ATask_IsTakenByExactlyOneWorker_EvenUnderConcurrency()
    {
        using var app = new TestApp();
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        var c = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("t"))).Content.ReadFromJsonAsync<ChannelDto>())!;
        var task = (await (await alice.PostAsJsonAsync($"/api/workspace/channels/{c.Id}/tasks", new NewTaskRequest("uno", "", NewTaskRequest.Me))).Content.ReadFromJsonAsync<TaskDto>())!;

        var store = app.Services.GetRequiredService<WorkspaceStore>();
        Assert.Null(await store.StartTaskAsync(task.Id)); // di una persona: un job rimasto in coda non lo prende
        // Lo stesso task in mano a un agente (senza metterlo in coda: i job sono quelli qui sotto).
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<AppDb>>().CreateDbContextAsync())
            await db.Tasks.Where(t => t.Id == task.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.AssigneeIsAgent, true).SetProperty(t => t.Assignee, "Cindy"));
        var winners = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.StartTaskAsync(task.Id))));
        Assert.Single(winners, w => w is not null);
    }
}

public sealed class Phase7AccountTests
{
    const string Alice = "alice@test.it";

    [Fact]
    public async Task ChangePassword_ClosesOtherSessions_AndTheNewOneWorks()
    {
        using var app = new TestApp();
        await app.CreateUserAsync(Alice);
        using var laptop = await app.LoginAsync(Alice);
        using var phone = await app.LoginAsync(Alice);
        var token = (await (await laptop.PostAsJsonAsync("/api/account/tokens", new NewApiTokenRequest("mcp"))).Content.ReadFromJsonAsync<ApiTokenDto>())!.Token;
        using var mcp = app.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Task<HttpStatusCode> McpStatus() => mcp.PostAsJsonAsync("/mcp", new { }).ContinueWith(t => t.Result.StatusCode); // i token valgono solo per /mcp
        Assert.NotEqual(HttpStatusCode.Unauthorized, await McpStatus());

        var r = await laptop.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest(TestApp.Password, "Nuova!Pass9"));
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/workspace/channels")).StatusCode);       // chi l'ha cambiata resta dentro
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/workspace/channels")).StatusCode); // gli altri no
        Assert.Equal(HttpStatusCode.Unauthorized, await McpStatus()); // nemmeno i token

        using var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/identity/login", new { email = Alice, password = TestApp.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/identity/login", new { email = Alice, password = "Nuova!Pass9" })).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_NeedsTheCurrentOne()
    {
        using var app = new TestApp();
        await app.CreateUserAsync(Alice);
        using var http = await app.LoginAsync(Alice);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest("sbagliata", "Nuova!Pass9"))).StatusCode);
    }

    [Fact]
    public async Task ResetPassword_FromTheTerminal_WritesAPrivateFile_AndUnlocksTheAccount()
    {
        using var app = new TestApp();
        await app.CreateUserAsync(Alice);
        using var old = await app.LoginAsync(Alice);
        var file = Path.Combine(app.Dir, "credenziali.txt");

        Assert.Equal(0, await AccountMaintenance.ResetPasswordAsync(app.Services, Alice, file));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        var password = File.ReadAllLines(file).Single(l => l.StartsWith("Password: "))["Password: ".Length..];
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/workspace/channels")).StatusCode); // sessioni vecchie chiuse
        using var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/identity/login", new { email = Alice, password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/identity/login", new { email = Alice, password = TestApp.Password })).StatusCode);
    }
}
