using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using A2A;
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Workspace;
using AgentWeb.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentWeb.Tests;

// Finto LLM su una porta locale: parla OpenAI (/v1/chat/completions, /v1/models), Anthropic (/v1/messages) e Ollama (/api/tags).
// Per OpenAI la risposta dipende dal modello richiesto:
//   "capo"    -> al primo giro chiama lo strumento ask_agent verso Esperto, al secondo compone "sintesi: <risposta>"
//   altrimenti -> il testo fisso passato a StartAsync
public sealed class FakeLlm : IAsyncDisposable
{
    readonly WebApplication app;
    public string Url { get; }
    public List<string> Requests { get; } = [];
    // Per modello: gli strumenti offerti nell'ultima richiesta e il system prompt ricevuto.
    public System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> ToolsSeen { get; } = new();
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> SystemSeen { get; } = new();

    FakeLlm(WebApplication app, string url) { this.app = app; Url = url; }

    public static async Task<FakeLlm> StartAsync(string reply)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();
        FakeLlm? self = null;
        app.MapPost("/v1/chat/completions", async (HttpRequest r) =>
        {
            using var body = await JsonDocument.ParseAsync(r.Body);
            var model = body.RootElement.GetProperty("model").GetString();
            lock (self!.Requests) self.Requests.Add($"openai {r.Headers.Authorization} {model}");
            if (model?.StartsWith("lento") == true) await Task.Delay(1500); // per misurare il parallelismo
            var toolResult = body.RootElement.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("role").GetString() == "tool").Select(m => m.GetProperty("content").ToString()).FirstOrDefault();
            self.ToolsSeen[model!] = body.RootElement.TryGetProperty("tools", out var tools)
                ? tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!).ToList() : [];
            self.SystemSeen[model!] = body.RootElement.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("role").GetString() == "system").Select(m => m.GetProperty("content").ToString()).FirstOrDefault() ?? "";
            // "tool:<nome>:<argomenti JSON>": al primo giro chiama lo strumento, al secondo risponde "fatto: <risultato>".
            if (model!.StartsWith("tool:"))
            {
                var parts = model.Split(':', 3);
                if (toolResult is null)
                    return Completion(null, "tool_calls", new[] { new { id = "call_t", type = "function", function = new { name = parts[1], arguments = parts[2].Replace("{URL}", self.Url) } } });
                return Completion($"fatto: {toolResult}", "stop", null);
            }
            if (model == "capo" && toolResult is null)
                return Completion(content: null, finish: "tool_calls", toolCalls: new[]
                {
                    new { id = "call_1", type = "function", function = new { name = "ask_agent", arguments = """{"agent":"Esperto","question":"dammi un parere"}""" } },
                });
            return Completion(model == "capo" ? $"sintesi: {toolResult}" : reply, "stop", null);
        });
        app.MapGet("/v1/models", () => Results.Json(new { data = new[] { new { id = "modello-b" }, new { id = "modello-a" } } }));
        app.MapGet("/pagina", () => Results.Content("<html><head><script>alert(1)</script></head><body><h1>Listino 2027</h1><p>Il piano Pro costa 42 euro.</p></body></html>", "text/html"));
        app.MapGet("/api/tags", () => Results.Json(new { models = new[] { new { name = "gemma4:e4b-mlx" } } }));
        app.MapPost("/v1/messages", (HttpRequest r) =>
        {
            lock (self!.Requests) self.Requests.Add("anthropic " + r.Headers.Authorization); // SDK Anthropic 13+: la chiave va in Authorization: Bearer
            return Results.Json(new
            {
                id = "msg_1", type = "message", role = "assistant", model = "finto",
                content = new[] { new { type = "text", text = reply } },
                stop_reason = "end_turn", stop_sequence = (string?)null,
                usage = new { input_tokens = 1, output_tokens = 1 },
            });
        });
        await app.StartAsync();
        self = new FakeLlm(app, app.Urls.First());
        return self;
    }

    static IResult Completion(string? content, string finish, object? toolCalls) => Results.Json(new
    {
        id = "chatcmpl-1", @object = "chat.completion", created = 1, model = "finto",
        choices = new[] { new { index = 0, message = new { role = "assistant", content, tool_calls = toolCalls }, finish_reason = finish } },
        usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
    });

    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}

public sealed class Phase6ProviderTests
{
    [Fact]
    public void Validation_PerAgent_CatchesMissingKeyAndModel()
    {
        var s = new AgentSettings
        {
            LocalAgents =
            [
                new() { Name = "A", Prompt = "p", Llm = new() { Kind = LlmKinds.Anthropic, Url = "https://api.anthropic.com", DefaultModel = "m" } },
                new() { Name = "B", Prompt = "p", Llm = new() { Kind = LlmKinds.OllamaLocal, Url = "http://localhost:11434" } },
            ],
        };
        var errors = s.Validate(allowStdio: false);
        Assert.Contains(errors, e => e.Contains("\"A\"") && e.Contains("token API"));
        Assert.Contains(errors, e => e.Contains("\"B\"") && e.Contains("modello"));
    }

    // Il tipo scelto non torna mai "Ollama locale" da solo: neppure per un agente disabilitato salvato senza modello.
    [Fact]
    public void ChosenKind_SurvivesReload_EvenWithoutAModel()
    {
        var json = """
            {"LocalAgents":[{"Name":"A","Prompt":"p","Enabled":false,"Llm":{"Kind":"anthropic","Url":"https://api.anthropic.com","ApiKey":"k","DefaultModel":""}},
                            {"Name":"B","Prompt":"p","Llm":{"Kind":"openai","Url":"https://api.openai.com/v1","DefaultModel":"gpt-5-mini"}},
                            {"Name":"C","Prompt":"p"}]}
            """;
        var s = JsonSerializer.Deserialize<AgentSettings>(json)!.Normalize("http://ollama:11434", "default");
        Assert.Equal((LlmKinds.Anthropic, "k", ""), (s.LocalAgents[0].Llm.Kind, s.LocalAgents[0].Llm.ApiKey, s.LocalAgents[0].Llm.DefaultModel));
        Assert.Equal((LlmKinds.OpenAI, "gpt-5-mini"), (s.LocalAgents[1].Llm.Kind, s.LocalAgents[1].Llm.DefaultModel));
        Assert.Equal((LlmKinds.OllamaLocal, "http://ollama:11434", "default"), (s.LocalAgents[2].Llm.Kind, s.LocalAgents[2].Llm.Url, s.LocalAgents[2].Llm.DefaultModel));
    }

    [Fact]
    public void OldSettings_WithSeparateProviders_AreConvertedIntoTheAgents()
    {
        var json = """
            {"LocalAgents":[{"Name":"A","Prompt":"p","Provider":"cloud","Model":"gpt-oss:120b"},{"Name":"B","Prompt":"p"}],
             "Providers":[{"Name":"locale","Kind":"ollama-local","Url":"http://localhost:11434","DefaultModel":"gemma"},
                          {"Name":"cloud","Kind":"ollama-cloud","Url":"https://ollama.com","ApiKey":"k","DefaultModel":"x"}]}
            """;
        var s = JsonSerializer.Deserialize<AgentSettings>(json)!.Normalize("http://localhost:11434", "default");
        Assert.Equal((LlmKinds.OllamaCloud, "k", "gpt-oss:120b"), (s.LocalAgents[0].Llm.Kind, s.LocalAgents[0].Llm.ApiKey, s.LocalAgents[0].Llm.DefaultModel));
        Assert.Equal((LlmKinds.OllamaLocal, "gemma"), (s.LocalAgents[1].Llm.Kind, s.LocalAgents[1].Llm.DefaultModel));
        Assert.DoesNotContain("Providers", JsonSerializer.Serialize(s)); // salvando, il formato vecchio sparisce
    }

    [Theory]
    [InlineData(LlmKinds.OpenAI, "openai Bearer chiave-openai finto")]
    [InlineData(LlmKinds.Anthropic, "anthropic Bearer chiave-anthropic")]
    public async Task ProviderTest_TalksTheRightProtocol_WithTheKey(string kind, string expectedRequest)
    {
        await using var fake = await FakeLlm.StartAsync("ok");
        var key = kind == LlmKinds.OpenAI ? "chiave-openai" : "chiave-anthropic";
        var url = kind == LlmKinds.OpenAI ? fake.Url + "/v1" : fake.Url;
        var result = await LlmClients.TestAsync(new LlmProviderConfig { Kind = kind, Url = url, ApiKey = key, DefaultModel = "finto" }, default);
        Assert.Contains("risponde: ok", result);
        Assert.Equal([expectedRequest], fake.Requests);
    }

    [Theory]
    [InlineData(LlmKinds.OllamaLocal, "", "gemma4:e4b-mlx")]
    [InlineData(LlmKinds.OpenAI, "/v1", "modello-a,modello-b")]
    [InlineData(LlmKinds.Anthropic, "", "modello-a,modello-b")]
    public async Task LoadModels_ReadsTheServerList(string kind, string path, string expected)
    {
        await using var fake = await FakeLlm.StartAsync("ok");
        var models = await LlmClients.ListModelsAsync(new LlmProviderConfig { Kind = kind, Url = fake.Url + path, ApiKey = "k" }, default);
        Assert.Equal(expected, string.Join(",", models));
    }
}

// Agenti che collaborano, esposti via MCP e A2A, con un LLM OpenAI-compatibile finto.
public sealed class Phase6AgentsTests : IAsyncLifetime
{
    TestApp app = null!;
    FakeLlm llm = null!;

    public async Task InitializeAsync()
    {
        llm = await FakeLlm.StartAsync("parere dell'esperto");
        app = new TestApp();
        LlmProviderConfig Llm(string model) => new() { Kind = LlmKinds.OpenAI, Url = llm.Url + "/v1", ApiKey = "k", DefaultModel = model };
        var settings = new AgentSettings
        {
            LocalAgents =
            [
                new() { Name = "Capo", Prompt = "Coordini il lavoro.", Llm = Llm("capo") },
                new() { Name = "Esperto", Prompt = "Dai pareri tecnici.", Protocol = AgentProtocols.Mcp, Llm = Llm("esperto") },
                new() { Name = "Pubblico", Prompt = "Agente esposto via A2A.", Protocol = AgentProtocols.A2A, Llm = Llm("esperto") },
            ],
        };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(settings));
        await app.CreateUserAsync("alice@test.it");
        await app.CreateUserAsync("bob@test.it");
    }

    public async Task DisposeAsync() { app.Dispose(); await llm.DisposeAsync(); }

    async Task<string> TokenAsync(string user)
    {
        using var http = await app.LoginAsync(user);
        var r = await http.PostAsJsonAsync("/api/account/tokens", new NewApiTokenRequest("test"));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ApiTokenDto>())!.Token;
    }

    Task<McpClient> McpAsync(string token)
    {
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(app.Server.BaseAddress, "mcp"), TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: true);
        return McpClient.CreateAsync(transport);
    }

    static string Text(CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    // Il cuore della richiesta: un task affidato a un agente che, per svolgerlo, consulta un altro agente.
    [Fact]
    public async Task Task_IsCoordinatedByOneAgent_ThatAsksAnotherAgent()
    {
        using var alice = await app.LoginAsync("alice@test.it");
        var channel = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("progetto"))).Content.ReadFromJsonAsync<ChannelDto>())!;
        foreach (var a in new[] { "Capo", "Esperto" })
            (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel.Id}/agents", new { name = a })).EnsureSuccessStatusCode();
        var task = (await (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel.Id}/tasks", new NewTaskRequest("analisi", "serve un parere tecnico", "Capo"))).Content.ReadFromJsonAsync<TaskDto>())!;

        TaskDto? done = null;
        for (var i = 0; i < 100 && done is null; i++)
        {
            await Task.Delay(100);
            done = (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{channel.Id}/tasks"))!
                .FirstOrDefault(t => t.Id == task.Id && t.Status is WorkStatus.Done or WorkStatus.Failed);
        }
        Assert.NotNull(done);
        Assert.Equal(WorkStatus.Done, done.Status);
        Assert.Contains("sintesi:", done.Result);
        Assert.Contains("parere dell'esperto", done.Result); // il risultato usa la risposta dell'altro agente

        var messages = (await alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{channel.Id}/messages"))!;
        Assert.Contains(messages, m => m.Author == "Capo" && m.Text == "→ chiedo a Esperto: dammi un parere");
        Assert.Contains(messages, m => m.Author == "Esperto" && m.Text == "parere dell'esperto");
        Assert.Contains(llm.Requests, r => r.EndsWith(" esperto")); // Esperto ha usato il suo LLM
    }

    [Fact]
    public async Task WithoutAToken_McpAnswers401()
    {
        using var http = app.CreateClient();
        var r = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task ApiToken_DoesNotOpenTheRestOfTheApi()
    {
        using var http = app.Bearer(await TokenAsync("alice@test.it"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/workspace/channels")).StatusCode);
    }

    [Fact]
    public async Task McpTools_RespectChannelVisibility()
    {
        using (var alice = await app.LoginAsync("alice@test.it"))
            (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("segreto", true))).EnsureSuccessStatusCode();

        await using var bob = await McpAsync(await TokenAsync("bob@test.it"));
        var tools = (await bob.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "list_channels", "read_messages", "send_message", "list_tasks", "create_task", "set_task_status", "list_agents", "ask_agent" }, tools);
        Assert.DoesNotContain("segreto", Text(await bob.CallToolAsync("list_channels")));
        var write = await bob.CallToolAsync("send_message", new Dictionary<string, object?> { ["channel"] = "segreto", ["text"] = "intrusione" });
        Assert.True(write.IsError);
    }

    [Fact]
    public async Task OnlyMcpAgents_AreReachableFromMcp()
    {
        await using var mcp = await McpAsync(await TokenAsync("alice@test.it"));
        using (var alice = await app.LoginAsync("alice@test.it"))
        {
            var lavoro = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("lavoro"))).Content.ReadFromJsonAsync<ChannelDto>())!;
            (await alice.PostAsJsonAsync($"/api/workspace/channels/{lavoro.Id}/agents", new { name = "Esperto" })).EnsureSuccessStatusCode();
        }

        var listed = Text(await mcp.CallToolAsync("list_agents"));
        Assert.Contains("Esperto", listed);
        Assert.DoesNotContain("Capo", listed); // interno
        Assert.DoesNotContain("Pubblico", listed); // solo A2A

        var internalAgent = await mcp.CallToolAsync("ask_agent", new Dictionary<string, object?> { ["agent"] = "Capo", ["channel"] = "lavoro", ["question"] = "x" });
        Assert.True(internalAgent.IsError);

        var answer = await mcp.CallToolAsync("ask_agent", new Dictionary<string, object?>
            { ["agent"] = "Esperto", ["channel"] = "lavoro", ["question"] = "come va?", ["timeout_seconds"] = 30 });
        Assert.NotEqual(true, answer.IsError); // IsError è null quando il tool riesce
        Assert.Equal("parere dell'esperto", Text(answer));
    }

    [Fact]
    public async Task RevokedToken_StopsWorkingImmediately()
    {
        var token = await TokenAsync("alice@test.it");
        await using (var mcp = await McpAsync(token)) await mcp.CallToolAsync("list_channels");
        using (var alice = await app.LoginAsync("alice@test.it"))
        {
            var sessions = await alice.GetFromJsonAsync<List<SessionDto>>("/api/account/sessions");
            (await alice.PostAsync($"/api/account/sessions/{sessions!.Single(s => s.Kind == "token").Id}/revoke", null)).EnsureSuccessStatusCode();
        }
        using var http = app.Bearer(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" })).StatusCode);
    }

    [Fact]
    public async Task A2AAgent_PublishesItsCard_AndAnswersWithAToken()
    {
        using var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/a2a/Capo/.well-known/agent-card.json")).StatusCode); // interno: non esiste per A2A
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/a2a/Pubblico", new { jsonrpc = "2.0", id = 1, method = "SendMessage" })).StatusCode);

        var http = app.Bearer(await TokenAsync("alice@test.it"));
        var card = await new A2ACardResolver(new Uri(app.Server.BaseAddress, "a2a/Pubblico/"), http).GetAgentCardAsync();
        Assert.Equal("Pubblico", card.Name);
        var client = A2AClientFactory.Create(card, http);
        Assert.Equal("parere dell'esperto", await AgentConnections.AskA2AAsync(client, "ciao"));
    }
}

// Contro un Ollama vero: AGENTWEB_TEST_OLLAMA=http://localhost:11434 AGENTWEB_TEST_OLLAMA_MODEL=gemma4:e4b-mlx
public sealed class OllamaFactAttribute : FactAttribute
{
    public OllamaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTWEB_TEST_OLLAMA") is null) Skip = "Imposta AGENTWEB_TEST_OLLAMA per provare un Ollama vero.";
    }
}

public sealed class Phase6RealOllamaTests
{
    static LlmProviderConfig Config => new()
    {
        Kind = LlmKinds.OllamaLocal, Url = Environment.GetEnvironmentVariable("AGENTWEB_TEST_OLLAMA")!,
        DefaultModel = Environment.GetEnvironmentVariable("AGENTWEB_TEST_OLLAMA_MODEL") ?? "gemma4:e4b-mlx",
    };

    [OllamaFact]
    public async Task LocalOllama_ListsModels_AndAnswers()
    {
        Assert.Contains(Config.DefaultModel, await LlmClients.ListModelsAsync(Config, default));
        var result = await LlmClients.TestAsync(Config, default);
        Assert.Contains("risponde:", result);
        Assert.DoesNotContain("(vuoto)", result);
    }
}

// Collaborazione con un modello vero (lento: minuti). Stesso gate di Phase6RealOllamaTests.
public sealed class Phase6RealOllamaCollaborationTests
{
    [OllamaFact]
    public async Task RealModel_DelegatesToAnotherAgent()
    {
        var llm = new LlmProviderConfig
        {
            Kind = LlmKinds.OllamaLocal, Url = Environment.GetEnvironmentVariable("AGENTWEB_TEST_OLLAMA")!,
            DefaultModel = Environment.GetEnvironmentVariable("AGENTWEB_TEST_OLLAMA_MODEL") ?? "gemma4:e4b-mlx",
        };
        using var app = new TestApp();
        var settings = new AgentSettings
        {
            LocalAgents =
            [
                new() { Name = "Capo", Prompt = "Coordini i task. Prima di rispondere chiedi SEMPRE un parere all'agente Esperto con lo strumento ask_agent, poi riassumi in una frase. Rispondi in italiano.", Llm = llm },
                new() { Name = "Esperto", Prompt = "Sei un esperto di database. Rispondi in italiano, massimo 40 parole.", Llm = llm },
            ],
        };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(settings));
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        var channel = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("reale"))).Content.ReadFromJsonAsync<ChannelDto>())!;
        foreach (var a in new[] { "Capo", "Esperto" }) await alice.PostAsJsonAsync($"/api/workspace/channels/{channel.Id}/agents", new { name = a });
        await alice.PostAsJsonAsync($"/api/workspace/channels/{channel.Id}/tasks", new NewTaskRequest("Postgres o SQLite per AgentWeb?", "", "Capo"));

        TaskDto? done = null;
        for (var i = 0; i < 300 && done is null; i++)
        {
            await Task.Delay(1000);
            done = (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{channel.Id}/tasks"))!.FirstOrDefault(t => t.Status is WorkStatus.Done or WorkStatus.Failed);
        }
        var messages = (await alice.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{channel.Id}/messages"))!;
        var log = string.Join("\n", messages.Select(m => $"[{m.Author}] {m.Text}"));
        Assert.True(done?.Status == WorkStatus.Done, log);
        Assert.Contains(messages, m => m.Author == "Capo" && m.Text.StartsWith("→ chiedo a Esperto"));
        Assert.Contains(messages, m => m.Author == "Esperto");
    }
}
