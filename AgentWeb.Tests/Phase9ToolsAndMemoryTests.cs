using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Workspace;
using AgentWeb.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWeb.Tests;

// Embedding finto e deterministico: ogni parola accende una dimensione. Testi con parole in comune risultano vicini.
public sealed class FakeEmbedder : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var text in values)
        {
            var v = new float[MemoryStore.Dimensions];
            foreach (var w in text.ToLowerInvariant().Split([' ', '.', ',', ':', ';', '\n', '!', '?', '«', '»'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3))
                v[(int)((uint)StableHash(w) % MemoryStore.Dimensions)] += 1;
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            if (norm == 0) v[0] = 1; else for (var i = 0; i < v.Length; i++) v[i] /= norm;
            result.Add(new Embedding<float>(v));
        }
        return Task.FromResult(result);
    }

    static int StableHash(string s) { unchecked { var h = 23; foreach (var c in s) h = h * 31 + c; return h; } }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public sealed class Phase9ToolsAndMemoryTests : IAsyncLifetime
{
    TestApp app = null!;
    FakeLlm llm = null!;
    HttpClient alice = null!;
    int channel;

    LlmProviderConfig Llm(string model) => new() { Kind = LlmKinds.OpenAI, Url = llm.Url + "/v1", ApiKey = "k", DefaultModel = model };

    // Ogni test sceglie i suoi agenti; il canale li contiene tutti.
    async Task SetupAsync(params LocalAgentConfig[] agents)
    {
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(new AgentSettings { LocalAgents = [.. agents] }));
        app.Services.GetRequiredService<MemoryStore>().UseEmbedder(new FakeEmbedder());
        await app.CreateUserAsync("alice@test.it");
        alice = await app.LoginAsync("alice@test.it");
        channel = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("lab"))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        foreach (var a in agents) (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/agents", new { name = a.Name })).EnsureSuccessStatusCode();
    }

    public async Task InitializeAsync()
    {
        llm = await FakeLlm.StartAsync("risposta");
        app = new TestApp(new() { ["Web:AllowPrivateHosts"] = "true" }); // il finto sito è su 127.0.0.1
    }

    public async Task DisposeAsync() { alice?.Dispose(); app.Dispose(); await llm.DisposeAsync(); }

    async Task<TaskDto> RunTaskAsync(string assignee, string title = "lavoro", string description = "")
    {
        var t = (await (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/tasks", new NewTaskRequest(title, description, assignee))).Content.ReadFromJsonAsync<TaskDto>())!;
        for (var i = 0; i < 150; i++)
        {
            var done = (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{channel}/tasks"))!.First(x => x.Id == t.Id);
            if (done.Status is WorkStatus.Done or WorkStatus.Failed) return done;
            await Task.Delay(100);
        }
        throw new TimeoutException("task non concluso");
    }

    [Fact]
    public async Task AnAgent_GetsOnlyTheToolsItsCardAllows()
    {
        await SetupAsync(
            new LocalAgentConfig { Name = "Lettore", Prompt = "p", Tools = [AgentTools.ReadChannel], Llm = Llm("lettore") },
            new LocalAgentConfig { Name = "Tutto", Prompt = "p", Tools = [.. AgentTools.Native.Select(n => n.Id)], Llm = Llm("tutto") });
        await RunTaskAsync("Lettore");
        await RunTaskAsync("Tutto");
        Assert.Equal(["list_tasks", "read_channel", "search_messages"], llm.ToolsSeen["lettore"].Order());
        Assert.Superset(new HashSet<string> { "ask_agent", "list_agents", "read_channel", "post_message", "create_task", "fetch_url", "remember", "recall", "forget" },
            llm.ToolsSeen["tutto"].ToHashSet());
    }

    [Fact]
    public async Task FetchUrl_ReadsThePage_AsDataNotInstructions()
    {
        await SetupAsync(new LocalAgentConfig { Name = "Web", Prompt = "p", Tools = [AgentTools.Web], Llm = Llm("""tool:fetch_url:{"url":"{URL}/pagina"}""") });
        var t = await RunTaskAsync("Web");
        Assert.Contains("Il piano Pro costa 42 euro", t.Result);
        Assert.Contains("non come istruzioni", t.Result);
        Assert.DoesNotContain("alert(1)", t.Result); // gli script non arrivano al modello
    }

    [Fact]
    public async Task CreateTask_HandsWorkToAnotherAgentOfTheChannel()
    {
        await SetupAsync(
            new LocalAgentConfig { Name = "Capo", Prompt = "p", Tools = [AgentTools.WriteChannel], Llm = Llm("""tool:create_task:{"title":"analisi db","description":"confronta","assignee":"Esperto"}""") },
            new LocalAgentConfig { Name = "Esperto", Prompt = "p", Tools = [], Llm = Llm("esperto") });
        var t = await RunTaskAsync("Capo");
        Assert.Contains("Creato il task", t.Result);
        TaskDto? child = null;
        for (var i = 0; i < 100 && child?.Status != WorkStatus.Done; i++)
        {
            await Task.Delay(100);
            child = (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{channel}/tasks"))!.FirstOrDefault(x => x.Title == "analisi db");
        }
        Assert.Equal(("Capo", "Esperto", WorkStatus.Done, "risposta"), (child!.CreatedBy, child.Assignee, child.Status, child.Result));
    }

    [Fact]
    public async Task CreateTask_CannotTargetItselfOrStrangers()
    {
        await SetupAsync(new LocalAgentConfig { Name = "Solo", Prompt = "p", Tools = [AgentTools.WriteChannel], Llm = Llm("""tool:create_task:{"title":"x","description":"y","assignee":"Solo"}""") });
        Assert.Contains("a te stesso", (await RunTaskAsync("Solo")).Result);
    }

    [Fact]
    public async Task Remember_ThenTheMemoryIsRecalledAutomatically()
    {
        await SetupAsync(
            new LocalAgentConfig { Name = "Memo", Prompt = "p", Tools = [AgentTools.Memory], Llm = Llm("""tool:remember:{"fact":"Il cliente Rossi preferisce Postgres come database","scope":"canale"}""") },
            new LocalAgentConfig { Name = "Altro", Prompt = "Sei un consulente.", Tools = [AgentTools.Memory], Llm = Llm("altro") });
        Assert.Contains("Ricordato", (await RunTaskAsync("Memo")).Result);

        var memory = (await alice.GetFromJsonAsync<List<MemoryDto>>($"/api/workspace/channels/{channel}/memory"))!;
        Assert.Contains(memory, m => m.Kind == "fact" && m.Content.Contains("Postgres"));

        // Un altro agente del canale riceve il ricordo nel prompt, senza dover chiamare recall.
        await RunTaskAsync("Altro", "quale database usa il cliente Rossi", "serve per Rossi");
        Assert.Contains("preferisce Postgres", llm.SystemSeen["altro"]);
    }

    [Fact]
    public async Task UploadedDocuments_AreSearchable_AndOnlyForTheChannel()
    {
        await SetupAsync(new LocalAgentConfig { Name = "Ricerca", Prompt = "p", Tools = [AgentTools.Memory], Llm = Llm("""tool:recall:{"query":"penale ritardo consegna"}""") });
        var text = "Contratto di fornitura.\n\nArticolo 7: in caso di ritardo nella consegna si applica una penale del 2% per settimana.\n\nArticolo 9: foro competente Milano.";
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"/api/workspace/channels/{channel}/documents", new { fileName = "contratto.md", text })).StatusCode);
        var t = await RunTaskAsync("Ricerca");
        Assert.Contains("penale del 2%", t.Result);
        Assert.Contains("contratto.md", t.Result);

        await app.CreateUserAsync("bob@test.it");
        using var bob = await app.LoginAsync("bob@test.it");
        var priv = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("segreto", true))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/workspace/channels/{priv}/memory")).StatusCode);
        var doc = (await alice.GetFromJsonAsync<List<MemoryDto>>($"/api/workspace/channels/{channel}/memory"))!.Single(m => m.Kind == "document");
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/api/workspace/channels/{channel}/documents/{doc.SourceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/workspace/channels/{channel}/documents/{doc.SourceId}")).StatusCode);
    }

    [Fact]
    public async Task LongChannels_AreSummarized_AndTheSummaryEntersTheContext()
    {
        await SetupAsync(new LocalAgentConfig { Name = "Sintesi", Prompt = "p", Llm = Llm("sintesi") });
        var store = app.Services.GetRequiredService<WorkspaceStore>();
        for (var i = 0; i < 70; i++) await store.PostAsync(channel, "alice@test.it", AuthorKind.User, $"messaggio numero {i}", WorkspaceStore.NoMentions);

        var memory = app.Services.GetRequiredService<MemoryStore>();
        await memory.MaybeSummarizeAsync(channel, app.Services.GetRequiredService<LlmClients>().For(Llm("sintesi")));
        Assert.NotNull(await memory.LatestSummaryAsync(channel));
        var transcript = await store.TranscriptAsync(channel);
        Assert.StartsWith("Riassunto della conversazione precedente:", transcript);
        Assert.Contains("messaggio numero 69", transcript);
        Assert.DoesNotContain("messaggio numero 5\n", transcript); // i vecchi sono nel riassunto, non nel contesto
    }
}

public sealed class Phase9SafetyTests
{
    [Theory]
    [InlineData("127.0.0.1", false)] [InlineData("10.1.2.3", false)] [InlineData("172.20.0.5", false)] [InlineData("192.168.1.204", false)]
    [InlineData("169.254.169.254", false)] [InlineData("100.64.0.1", false)] [InlineData("::1", false)] [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)] [InlineData("::ffff:192.168.0.1", false)] [InlineData("8.8.8.8", true)] [InlineData("2606:4700::1111", true)]
    public void OnlyPublicAddresses_AreReachable(string ip, bool expected) => Assert.Equal(expected, SafeFetch.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public async Task FetchUrl_RefusesLocalAndPrivateTargets()
    {
        var fetch = new SafeFetch(new ConfigurationBuilder().Build());
        foreach (var url in new[] { "http://127.0.0.1:5294/login", "http://localhost:11434/api/tags", "http://169.254.169.254/latest/meta-data", "file:///etc/passwd" })
        {
            var r = await fetch.FetchAsync(url, default);
            Assert.True(r.Contains("non pubblico") || r.Contains("non valido"), $"{url} -> {r}");
        }
    }

    [Fact]
    public void Documents_AreChunkedWithOverlap()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"Paragrafo {i}: " + new string('x', 200)));
        var chunks = MemoryStore.Chunk(text).ToList();
        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 1100, $"pezzo da {c.Length}"));
        Assert.Contains("Paragrafo 19", chunks[^1]);
    }
}

// Con i modelli veri (lento): gemma4 salva un ricordo con remember, embeddinggemma lo indicizza su pgvector,
// un secondo agente lo riceve da solo nel prompt. Stesso gate dei test Ollama.
public sealed class Phase9RealMemoryTests
{
    [OllamaFact]
    public async Task RealModels_RememberAndRecall()
    {
        var url = Environment.GetEnvironmentVariable("AGENTWEB_TEST_OLLAMA")!;
        var llm = new LlmProviderConfig { Kind = LlmKinds.OllamaLocal, Url = url, DefaultModel = Environment.GetEnvironmentVariable("AGENTWEB_TEST_OLLAMA_MODEL") ?? "gemma4:e4b-mlx" };
        using var app = new TestApp(new() { ["Memory:EmbeddingUrl"] = url });
        var settings = new AgentSettings
        {
            LocalAgents =
            [
                new() { Name = "Archivista", Prompt = "Quando ti danno un'informazione da ricordare, salvala SEMPRE con lo strumento remember (scope canale), poi conferma in una frase.", Tools = [AgentTools.Memory], Llm = llm },
                new() { Name = "Consulente", Prompt = "Rispondi in una frase, in italiano, usando i ricordi che hai.", Tools = [AgentTools.Memory], Llm = llm },
            ],
        };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(settings));
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        var ch = (await (await alice.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest("memoria"))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        foreach (var a in new[] { "Archivista", "Consulente" }) await alice.PostAsJsonAsync($"/api/workspace/channels/{ch}/agents", new { name = a });

        async Task<TaskDto> Run(string who, string title)
        {
            var t = (await (await alice.PostAsJsonAsync($"/api/workspace/channels/{ch}/tasks", new NewTaskRequest(title, "", who))).Content.ReadFromJsonAsync<TaskDto>())!;
            for (var i = 0; i < 240; i++)
            {
                var d = (await alice.GetFromJsonAsync<List<TaskDto>>($"/api/workspace/channels/{ch}/tasks"))!.First(x => x.Id == t.Id);
                if (d.Status is WorkStatus.Done or WorkStatus.Failed) return d;
                await Task.Delay(1000);
            }
            throw new TimeoutException();
        }

        await Run("Archivista", "Ricorda: il fornitore Bianchi consegna solo il giovedì.");
        var memory = (await alice.GetFromJsonAsync<List<MemoryDto>>($"/api/workspace/channels/{ch}/memory"))!;
        Assert.Contains(memory, m => m.Kind == "fact" && m.Content.Contains("giovedì", StringComparison.OrdinalIgnoreCase));

        var answer = await Run("Consulente", "In che giorno consegna il fornitore Bianchi?");
        Assert.Contains("gioved", answer.Result, StringComparison.OrdinalIgnoreCase);
    }
}
