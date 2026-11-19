using System.Collections.Concurrent;
using System.Text.Json;
using AgentWeb.Cli;
using AgentWeb.Shared.Workspace;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWeb.Tests;

// Fase 5 — CLI: un umano completa il flusso solo da terminale; la CLI non ha rotte privilegiate.
public sealed class Phase5CliTests : IDisposable
{
    readonly TestApp app = new();
    readonly ConcurrentBag<string> calls = []; // "METODO /percorso" di ogni richiesta della CLI

    sealed class Recorder(ConcurrentBag<string> calls, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            calls.Add($"{r.Method} {r.RequestUri!.AbsolutePath}");
            return base.SendAsync(r, ct);
        }
    }

    sealed record Run(int Code, string Out, string Err);

    // Ogni "terminale" ha la sua cartella di credenziali (= un utente diverso sulla stessa macchina).
    async Task<Run> Cli(string profile, string? stdin, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var env = new CliEnvironment
        {
            In = new StringReader(stdin ?? ""), Out = o, Err = e,
            ConfigDir = Path.Combine(app.Dir, "cli-" + profile),
            CreateHandler = () => new Recorder(calls, app.Server.CreateHandler()),
            DefaultServer = app.Server.BaseAddress.ToString(),
        };
        var code = await CliApp.RunAsync(args, env);
        return new(code, o.ToString().Trim(), e.ToString().Trim());
    }

    Task<Run> Login(string profile, string email) => Cli(profile, TestApp.Password, "login", "--email", email, "--password-stdin");

    [Fact]
    public async Task Human_CreatesChannel_Invites_Writes_Reads_OnlyFromTheCli()
    {
        await app.CreateUserAsync("alice@test.it");
        await app.CreateUserAsync("bob@test.it");
        Assert.Equal(0, (await Login("alice", "alice@test.it")).Code);
        Assert.Equal(0, (await Login("bob", "bob@test.it")).Code);

        var created = await Cli("alice", null, "channel", "create", "progetto", "--private", "--json");
        Assert.Equal(0, created.Code);
        var channel = JsonSerializer.Deserialize<ChannelDto>(created.Out, CliJson.Options)!;
        Assert.True(channel.IsPrivate);

        // Prima dell'invito bob non lo vede (stessa regola di web e app)...
        Assert.NotEqual(0, (await Cli("bob", null, "message", "read", "progetto")).Code);
        Assert.Equal(0, (await Cli("alice", null, "channel", "invite", "progetto", "bob@test.it")).Code);
        // ...dopo sì, scrive per nome, e alice legge.
        Assert.Equal(0, (await Cli("bob", null, "message", "send", "progetto", "ciao", "dal", "terminale")).Code);
        var read = await Cli("alice", null, "message", "read", "progetto", "--json");
        var messages = JsonSerializer.Deserialize<List<MessageDto>>(read.Out, CliJson.Options)!;
        Assert.Contains(messages, m => m.Author == "bob@test.it" && m.Text == "ciao dal terminale");

        var since = await Cli("alice", null, "message", "read", channel.Id.ToString(), "--since", messages[^2].Id.ToString(), "--json");
        Assert.Equal(["ciao dal terminale"], JsonSerializer.Deserialize<List<MessageDto>>(since.Out, CliJson.Options)!.Select(m => m.Text));
    }

    [Fact]
    public async Task Tasks_FromTheCli()
    {
        await app.CreateUserAsync("alice@test.it");
        await Login("alice", "alice@test.it");
        await Cli("alice", null, "channel", "create", "lavoro");
        var t = JsonSerializer.Deserialize<TaskDto>((await Cli("alice", null, "task", "create", "lavoro", "scrivere", "i", "test", "--assign", "me", "--json")).Out, CliJson.Options)!;
        Assert.Equal("alice@test.it", t.Assignee);
        Assert.Equal(0, (await Cli("alice", null, "task", "done", t.Id.ToString())).Code);
        var list = JsonSerializer.Deserialize<List<TaskDto>>((await Cli("alice", null, "task", "list", "lavoro", "--json")).Out, CliJson.Options)!;
        Assert.Equal(WorkStatus.Done, Assert.Single(list).Status);
    }

    [Fact]
    public async Task Threads_Unread_Search_FromTheCli()
    {
        await app.CreateUserAsync("alice@test.it");
        await app.CreateUserAsync("bob@test.it");
        await Login("alice", "alice@test.it");
        await Login("bob", "bob@test.it");
        await Cli("alice", null, "channel", "create", "rilasci");
        await Cli("alice", null, "message", "send", "rilasci", "versione", "candidata", "pronta");
        var root = JsonSerializer.Deserialize<List<MessageDto>>((await Cli("alice", null, "message", "read", "rilasci", "--json")).Out, CliJson.Options)!.Last();

        Assert.Equal(0, (await Cli("bob", null, "message", "reply", "rilasci", root.Id.ToString(), "la", "provo", "io")).Code);
        var thread = JsonSerializer.Deserialize<List<MessageDto>>((await Cli("alice", null, "message", "thread", "rilasci", root.Id.ToString(), "--json")).Out, CliJson.Options)!;
        Assert.Equal(["versione candidata pronta", "la provo io"], thread.Select(m => m.Text));

        var hits = JsonSerializer.Deserialize<List<SearchHitDto>>((await Cli("bob", null, "message", "search", "candidata", "--json")).Out, CliJson.Options)!;
        Assert.Equal(root.Id, Assert.Single(hits).Message.Id);

        Assert.Contains("non letti", (await Cli("alice", null, "channel", "list")).Out); // la risposta di bob
        Assert.Equal(0, (await Cli("alice", null, "channel", "read", "rilasci")).Code);
        Assert.DoesNotContain("non letti", (await Cli("alice", null, "channel", "list")).Out);
    }

    [Fact]
    public async Task Password_IsNeverAcceptedAsAnArgument()
    {
        await app.CreateUserAsync("alice@test.it");
        var r = await Cli("alice", null, "login", "--email", "alice@test.it", "--password", TestApp.Password);
        Assert.Equal(ExitCodes.Usage, r.Code);
        Assert.DoesNotContain(calls, c => c.Contains("login")); // rifiutato prima di contattare il server
    }

    [Fact]
    public async Task RevokedSession_ExitsWithCode3()
    {
        await app.CreateUserAsync("alice@test.it");
        await Login("laptop", "alice@test.it");
        await Login("telefono", "alice@test.it");
        Assert.Equal(0, (await Cli("telefono", null, "logout")).Code); // revoca solo la sessione del telefono
        Assert.Equal(ExitCodes.Unauthenticated, (await Cli("telefono", null, "channel", "list")).Code);
        Assert.Equal(0, (await Cli("laptop", null, "channel", "list")).Code);

        var sessions = JsonSerializer.Deserialize<List<SessionDto>>((await Cli("laptop", null, "sessions", "list", "--json")).Out, CliJson.Options)!;
        Assert.Equal(0, (await Cli("laptop", null, "sessions", "revoke", Assert.Single(sessions).Id)).Code);
        var after = await Cli("laptop", null, "channel", "list");
        Assert.Equal(ExitCodes.Unauthenticated, after.Code);
        Assert.Contains("agentweb login", after.Err);
    }

    [Fact]
    public async Task OnlyAdmins_CreateUsers_FromTheCli()
    {
        await app.CreateUserAsync("capo@test.it", admin: true);
        await app.CreateUserAsync("alice@test.it");
        await Login("capo", "capo@test.it");
        await Login("alice", "alice@test.it");
        Assert.Equal(ExitCodes.Error, (await Cli("alice", TestApp.Password, "user", "create", "x@test.it", "--password-stdin")).Code);
        Assert.Equal(0, (await Cli("capo", TestApp.Password, "user", "create", "nuovo@test.it", "--password-stdin")).Code);
        Assert.Equal(0, (await Login("nuovo", "nuovo@test.it")).Code);
    }

    [Fact]
    public async Task WithoutLogin_EveryCommandFailsWithCode3()
    {
        foreach (var cmd in new[] { new[] { "channel", "list" }, ["message", "read", "1"], ["agent", "list"], ["whoami"] })
            Assert.Equal(ExitCodes.Unauthenticated, (await Cli("nessuno", null, cmd)).Code);
    }

    // Nessuna rotta privilegiata: ogni chiamata della CLI colpisce un endpoint che esiste per tutti i client
    // e, a parte login/refresh, richiede autorizzazione. Se domani qualcuno aggiunge /api/cli/..., il test lo vede.
    [Fact]
    public async Task Cli_UsesOnlyThePublicAuthorizedApi()
    {
        await app.CreateUserAsync("alice@test.it");
        await Login("alice", "alice@test.it");
        await Cli("alice", null, "channel", "create", "c1");
        await Cli("alice", null, "message", "send", "c1", "x");
        await Cli("alice", null, "message", "read", "c1", "--since", "0");
        await Cli("alice", null, "task", "create", "c1", "t");
        await Cli("alice", null, "message", "search", "x");
        await Cli("alice", null, "channel", "read", "c1");
        await Cli("alice", null, "agent", "list");
        await Cli("alice", null, "whoami");
        await Cli("alice", null, "sessions", "list");
        await Cli("alice", null, "logout");

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        foreach (var call in calls.Distinct())
        {
            var path = call.Split(' ', 2)[1];
            var endpoint = endpoints.FirstOrDefault(e => Matches(e.RoutePattern, path));
            Assert.True(endpoint is not null, $"{call}: nessun endpoint");
            Assert.DoesNotContain("cli", endpoint!.RoutePattern.RawText!, StringComparison.OrdinalIgnoreCase);
            var anonymous = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null;
            Assert.True(!anonymous || path is "/identity/login" or "/identity/refresh", $"{call} non richiede autorizzazione");
        }
    }

    static bool Matches(RoutePattern p, string path)
    {
        var segs = path.Trim('/').Split('/');
        if (p.PathSegments.Count != segs.Length) return false;
        for (var i = 0; i < segs.Length; i++)
        {
            var part = p.PathSegments[i].Parts.Single();
            if (part is RoutePatternLiteralPart lit && !lit.Content.Equals(segs[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public void Dispose() => app.Dispose();
}
