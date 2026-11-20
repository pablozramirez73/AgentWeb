using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentWeb.Cli;
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Workspace;

namespace AgentWeb.Tests;

// Fase 11 — daemon: gli agenti "daemon" girano sul computer del loro utente, non nel server.
// Invarianti: il runtime non vede credenziali di AgentWeb; la revoca della sessione ferma subito daemon e runtime;
// il contenuto di un canale esce solo verso il computer di chi lo vede; una consegna abbandonata viene riconsegnata.
public sealed class Phase11DaemonTests : IAsyncLifetime
{
    readonly TestApp app = new(new() { ["Agents:DaemonLeaseSeconds"] = "2", ["Agents:DaemonPollSeconds"] = "1" });
    HttpClient alice = null!, bob = null!;
    string script = null!, envDump = null!, pidFile = null!;

    public async Task InitializeAsync()
    {
        envDump = Path.Combine(app.Dir, "env.txt");
        pidFile = Path.Combine(app.Dir, "pid.txt");
        script = Path.Combine(app.Dir, "eco.sh");
        // Runtime finto: legge il prompt da stdin; "DORMI" lo tiene occupato (per la revoca); salva l'ambiente che riceve.
        await File.WriteAllTextAsync(script, $$"""
            #!/bin/sh
            input=$(cat)
            env > '{{envDump}}'
            case "$input" in *DORMI*) echo $$ > '{{pidFile}}'; sleep 60;; esac
            echo "eco: ho letto ${#input} caratteri"
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var settings = new AgentSettings
        {
            LocalAgents = [new() { Name = "Locale", Prompt = "Sei un agente locale.", Llm = new() { Kind = LlmKinds.Daemon, Url = "alice@test.it", DefaultModel = "eco" } }],
        };
        await File.WriteAllTextAsync(Path.Combine(app.Dir, "agentsettings.json"), JsonSerializer.Serialize(settings));
        await app.CreateUserAsync("alice@test.it");
        await app.CreateUserAsync("bob@test.it");
        alice = await app.LoginAsync("alice@test.it");
        bob = await app.LoginAsync("bob@test.it");
    }

    public async Task DisposeAsync() { stop.Cancel(); if (daemon is not null) await daemon; app.Dispose(); }

    readonly CancellationTokenSource stop = new();
    readonly StringWriter daemonLog = new();
    Task<int>? daemon;

    string CliDir => Path.Combine(app.Dir, "cli-pc");

    CliEnvironment Env(string? stdin = null, CancellationToken cancel = default) => new()
    {
        In = new StringReader(stdin ?? ""), Out = new StringWriter(), Err = daemonLog,
        ConfigDir = CliDir, CreateHandler = () => app.Server.CreateHandler(), DefaultServer = app.Server.BaseAddress.ToString(),
        Cancel = cancel, DaemonHeartbeat = TimeSpan.FromMilliseconds(300),
    };

    async Task StartDaemonAsync()
    {
        Assert.Equal(0, await CliApp.RunAsync(["login", "--email", "alice@test.it", "--password-stdin"], Env(TestApp.Password)));
        daemon = Task.Run(() => CliApp.RunAsync(["daemon", "--runtime", $"eco={script}", "--machine", "pc-di-alice"], Env(cancel: stop.Token)));
        await Until(async () => (await alice.GetFromJsonAsync<List<MachineDto>>("/api/daemon/machines"))!.Count == 1, "il daemon non si è registrato");
    }

    async Task<int> ChannelWithAgentAsync(HttpClient owner, string name, bool isPrivate = false)
    {
        var c = (await (await owner.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest(name, isPrivate))).Content.ReadFromJsonAsync<ChannelDto>())!.Id;
        (await owner.PostAsJsonAsync($"/api/workspace/channels/{c}/agents", new { name = "Locale" })).EnsureSuccessStatusCode();
        return c;
    }

    static async Task<MessageDto> WaitMessageAsync(HttpClient http, int channel, Func<MessageDto, bool> match)
    {
        MessageDto? found = null;
        await Until(async () => (found = (await http.GetFromJsonAsync<List<MessageDto>>($"/api/workspace/channels/{channel}/messages"))!.FirstOrDefault(match)) is not null, "messaggio atteso non arrivato");
        return found!;
    }

    static async Task Until(Func<Task<bool>> condition, string why, int seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < end, why);
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Agent_RunsOnTheUsersMachine_WithoutAnyAgentWebCredential()
    {
        Environment.SetEnvironmentVariable("AGENTWEB_SEGRETO_DI_PROVA", "non-deve-uscire");
        await StartDaemonAsync();
        var c = await ChannelWithAgentAsync(alice, "laboratorio");
        await alice.PostAsJsonAsync($"/api/workspace/channels/{c}/messages", new NewMessageRequest("@Locale ciao"));

        var reply = await WaitMessageAsync(alice, c, m => m.Author == "Locale");
        Assert.StartsWith("eco: ho letto", reply.Text);

        var childEnv = await File.ReadAllTextAsync(envDump);
        var creds = JsonSerializer.Deserialize<Credentials>(await File.ReadAllTextAsync(Path.Combine(CliDir, "credentials.json")), CliJson.Options)!;
        Assert.DoesNotContain(creds.AccessToken, childEnv);
        Assert.DoesNotContain(creds.RefreshToken, childEnv);
        Assert.DoesNotContain("AGENTWEB", childEnv);
        Assert.DoesNotContain("ciao", daemonLog.ToString()); // il prompt non finisce nei log
    }

    [Fact]
    public async Task RevokingTheDaemonSession_KillsTheRuntime_AndStopsTheDaemon()
    {
        await StartDaemonAsync();
        var c = await ChannelWithAgentAsync(alice, "lungo");
        await alice.PostAsJsonAsync($"/api/workspace/channels/{c}/messages", new NewMessageRequest("@Locale DORMI"));
        await Until(() => Task.FromResult(File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0), "il runtime non è partito");
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());

        var sessions = await alice.GetFromJsonAsync<List<SessionDto>>("/api/account/sessions");
        foreach (var s in sessions!.Where(s => !s.Current)) // la sessione della CLI (quella del daemon)
            (await alice.PostAsync($"/api/account/sessions/{s.Id}/revoke", null)).EnsureSuccessStatusCode();

        var exit = await daemon!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ExitCodes.Unauthenticated, exit);
        await Until(() => Task.FromResult(!Alive(pid)), "il runtime è ancora vivo dopo la revoca", seconds: 5);
    }

    static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    // Con --as-user il runtime gira come un altro utente sotto sudo: la revoca deve ucciderlo comunque (non lasciarlo orfano).
    [AsUserFact]
    public async Task AsUser_RevokingTheSession_KillsTheRuntimeUnderSudo()
    {
        var runner = Environment.GetEnvironmentVariable("AGENTWEB_TEST_AS_USER")!;
        // L'altro utente deve poter leggere lo script e scrivere il pid: cartella aperta fuori dalla temp dell'utente corrente.
        var dir = Directory.CreateDirectory(Path.Combine("/tmp", "agentweb-asuser-" + Guid.NewGuid().ToString("N"))).FullName;
        File.SetUnixFileMode(dir, (UnixFileMode)0b111_111_111);
        var (sleeper, pids) = (Path.Combine(dir, "dorme.sh"), Path.Combine(dir, "pid.txt"));
        await File.WriteAllTextAsync(sleeper, $"#!/bin/sh\ncat >/dev/null\necho $$ > '{pids}'\nexec sleep 60\n");
        File.SetUnixFileMode(sleeper, (UnixFileMode)0b111_101_101);
        try
        {
            Assert.Equal(0, await CliApp.RunAsync(["login", "--email", "alice@test.it", "--password-stdin"], Env(TestApp.Password)));
            daemon = Task.Run(() => CliApp.RunAsync(["daemon", "--runtime", $"eco={sleeper}", "--machine", "pc-di-alice", "--as-user", runner], Env(cancel: stop.Token)));
            await Until(async () => (await alice.GetFromJsonAsync<List<MachineDto>>("/api/daemon/machines"))!.Count == 1, "il daemon non si è registrato");
            var c = await ChannelWithAgentAsync(alice, "sudo");
            await alice.PostAsJsonAsync($"/api/workspace/channels/{c}/messages", new NewMessageRequest("@Locale dormi"));
            await Until(() => Task.FromResult(File.Exists(pids) && File.ReadAllText(pids).Trim().Length > 0), "il runtime non è partito come " + runner);
            var pid = (await File.ReadAllTextAsync(pids)).Trim();

            foreach (var s in (await alice.GetFromJsonAsync<List<SessionDto>>("/api/account/sessions"))!.Where(s => !s.Current))
                (await alice.PostAsync($"/api/account/sessions/{s.Id}/revoke", null)).EnsureSuccessStatusCode();

            Assert.Equal(ExitCodes.Unauthenticated, await daemon!.WaitAsync(TimeSpan.FromSeconds(10)));
            // ps vede anche i processi di un altro utente (Process.GetProcessById non sempre).
            await Until(async () =>
            {
                using var ps = Process.Start(new ProcessStartInfo("ps", ["-p", pid]) { RedirectStandardOutput = true })!;
                await ps.WaitForExitAsync();
                return ps.ExitCode != 0;
            }, "il runtime sotto sudo è ancora vivo dopo la revoca", seconds: 5);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    [Fact]
    public async Task PrivateChannel_TheHostCannotSee_NeverReachesTheMachine()
    {
        await StartDaemonAsync();
        var c = await ChannelWithAgentAsync(bob, "di-bob", isPrivate: true); // alice non è membro
        await bob.PostAsJsonAsync($"/api/workspace/channels/{c}/messages", new NewMessageRequest("@Locale leggi il segreto"));

        var refused = await WaitMessageAsync(bob, c, m => m.Kind == AuthorKind.System && m.Text.Contains("non vede questo canale"));
        Assert.Contains("alice@test.it", refused.Text);
        Assert.DoesNotContain("→", daemonLog.ToString()); // nessuna consegna al daemon
        Assert.False(File.Exists(envDump));               // il runtime non è mai partito
    }

    [Fact]
    public async Task AbandonedDelivery_IsRedelivered_AndCompletedOnce()
    {
        var poll = new DaemonPollRequest("finto", ["eco"]);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsJsonAsync("/api/daemon/poll", poll)).StatusCode); // si registra
        var c = await ChannelWithAgentAsync(alice, "riconsegna");
        await alice.PostAsJsonAsync($"/api/workspace/channels/{c}/messages", new NewMessageRequest("@Locale ciao"));

        async Task<DaemonDelivery> TakeAsync()
        {
            DaemonDelivery? d = null;
            await Until(async () =>
            {
                var r = await alice.PostAsJsonAsync("/api/daemon/poll", poll);
                return r.StatusCode == HttpStatusCode.OK && (d = await r.Content.ReadFromJsonAsync<DaemonDelivery>()) is not null;
            }, "nessuna consegna");
            return d!;
        }

        var first = await TakeAsync();
        Assert.Contains("ciao", first.Prompt);
        // Nessun heartbeat: dopo il lease (2 s) la stessa consegna torna disponibile.
        var again = await TakeAsync();
        Assert.Equal(first.Id, again.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/daemon/deliveries/{again.Id}/complete", new DaemonResult("intruso", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsJsonAsync($"/api/daemon/deliveries/{again.Id}/complete", new DaemonResult("risposta locale", null))).StatusCode);
        await WaitMessageAsync(alice, c, m => m.Author == "Locale" && m.Text == "risposta locale");
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync($"/api/daemon/deliveries/{again.Id}/complete", new DaemonResult("doppione", null))).StatusCode);
    }

    // Due repliche sullo stesso database: il daemon è collegato alla prima, la menzione arriva alla seconda.
    [Fact]
    public async Task TwoReplicas_ShareTheQueue()
    {
        await StartDaemonAsync();
        using var other = new TestApp(new()
        {
            ["AgentSettings:Path"] = Path.Combine(app.Dir, "agentsettings.json"),
            ["Agents:DaemonLeaseSeconds"] = "2", ["Agents:DaemonPollSeconds"] = "1",
        }, sharedDatabase: app.Database);
        using var aliceThere = await other.LoginAsync("alice@test.it");
        Assert.Single((await aliceThere.GetFromJsonAsync<List<MachineDto>>("/api/daemon/machines"))!);

        var c = await ChannelWithAgentAsync(aliceThere, "due-repliche");
        await aliceThere.PostAsJsonAsync($"/api/workspace/channels/{c}/messages", new NewMessageRequest("@Locale ciao da lontano"));
        var reply = await WaitMessageAsync(aliceThere, c, m => m.Author == "Locale");
        Assert.StartsWith("eco: ho letto", reply.Text);
    }

    // Pagina Attività: il lavoro di un agente in un canale privato lo vede solo chi vede quel canale.
    [Fact]
    public async Task Activity_ShowsAgentRuns_OnlyToWhoSeesTheChannel()
    {
        await StartDaemonAsync();
        var c = await ChannelWithAgentAsync(alice, "riservata", isPrivate: true);
        await alice.PostAsJsonAsync($"/api/workspace/channels/{c}/messages", new NewMessageRequest("@Locale ciao"));
        await WaitMessageAsync(alice, c, m => m.Author == "Locale");

        ActivityDto? mine = null;
        await Until(async () => (mine = await alice.GetFromJsonAsync<ActivityDto>("/api/workspace/activity"))!.Recent.Count > 0, "nessun lavoro registrato");
        var run = Assert.Single(mine!.Recent);
        Assert.Equal(("Locale", c, "riservata", "risposta"), (run.Agent, run.ChannelId, run.Channel, run.Kind));
        Assert.Null(run.Error);
        Assert.NotNull(run.Seconds);

        var theirs = (await bob.GetFromJsonAsync<ActivityDto>("/api/workspace/activity"))!;
        Assert.Empty(theirs.Recent);
        Assert.Empty(theirs.Running);
    }

    [Fact]
    public async Task WithoutADaemon_TheChannelIsToldWhy()
    {
        var c = await ChannelWithAgentAsync(alice, "spento");
        await alice.PostAsJsonAsync($"/api/workspace/channels/{c}/messages", new NewMessageRequest("@Locale ci sei?"));
        var m = await WaitMessageAsync(alice, c, m => m.Kind == AuthorKind.System && m.Text.Contains("nessun daemon"));
        Assert.Contains("agentweb daemon", m.Text);
    }
}

public sealed class Phase11DaemonIsolationTests
{
    // Con --as-user il runtime parte tramite sudo come l'altro utente, senza shell e senza password (-n); ambiente comunque ripulito.
    [Fact]
    public void AsUser_RunsTheRuntimeThroughSudo()
    {
        Environment.SetEnvironmentVariable("AGENTWEB_TOKEN_DI_PROVA", "segreto");
        var psi = Daemon.StartInfo(Daemon.ParseRuntime("claude=claude -p"), [], "agentweb-runner");
        Assert.Equal("/usr/bin/sudo", psi.FileName);
        Assert.Equal(["-n", "-u", "agentweb-runner", "-H", "--", "claude", "-p"], psi.ArgumentList);
        Assert.DoesNotContain(psi.Environment.Keys, k => k.StartsWith("AGENTWEB_"));

        var plain = Daemon.StartInfo(Daemon.ParseRuntime("claude=claude -p"), [], null);
        Assert.Equal("claude", plain.FileName);
        Assert.Equal(["-p"], plain.ArgumentList);
    }

    [Theory]
    [InlineData("--as-user", "-root")]
    [InlineData("--as-user", "a b")]
    public async Task AsUser_RejectsBadNames_AndEnvPassthrough(string opt, string value)
    {
        var env = new CliEnvironment { In = new StringReader(""), Out = new StringWriter(), Err = new StringWriter(), ConfigDir = Directory.CreateTempSubdirectory().FullName };
        Assert.Equal(ExitCodes.Usage, await CliApp.RunAsync(["daemon", "--runtime", "eco=cat", opt, value], env));
        Assert.Equal(ExitCodes.Usage, await CliApp.RunAsync(["daemon", "--runtime", "eco=cat", "--as-user", "runner", "--env", "OPENAI_API_KEY"], env));
    }
}

// Contro sudo vero: AGENTWEB_TEST_AS_USER=<utente> e una regola sudoers "<tu> ALL=(<utente>) NOPASSWD: ALL".
public sealed class AsUserFactAttribute : FactAttribute
{
    public AsUserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTWEB_TEST_AS_USER") is null) Skip = "Imposta AGENTWEB_TEST_AS_USER (con regola sudoers NOPASSWD) per provare --as-user con sudo vero.";
    }
}
