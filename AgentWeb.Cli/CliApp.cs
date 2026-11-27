using System.Net.Http.Json;
using System.Text.Json;
using AgentWeb.Shared.Workspace;
using static AgentWeb.Shared.Lang;

namespace AgentWeb.Cli;

// `agentweb` — la stessa CLI per persone e agenti. Usa solo le API pubbliche del server con il token dell'utente:
// nessuna rotta riservata, stessa autorizzazione di web e app (ChannelVisibility, ruoli, sessioni revocabili).
public static class CliApp
{
    static string Help => T("cli.help");

    // Riassunto leggibile delle regole: quella eseguibile è Phase3AuthorizationTests.Matrix. Cambiando una regola, aggiornare entrambe.
    static string Manual => T("cli.manual");

    static readonly HashSet<string> Flags =["--json", "--private", "--password-stdin", "--help", "-h"];

    sealed record Args(List<string> Positional, Dictionary<string, string> Options, HashSet<string> Switches, Dictionary<string, List<string>> All)
    {
        public List<string> Many(string name) => All.GetValueOrDefault(name) ?? [];
        public bool Json => Switches.Contains("--json");
        public string? Opt(string name) => Options.GetValueOrDefault(name);
        public string At(int i, string what) => i < Positional.Count ? Positional[i] : throw new CliException(T("cli.missing", what), ExitCodes.Usage);
        public string Rest(int from, string what) => Positional.Count > from ? string.Join(' ', Positional.Skip(from)) : throw new CliException(T("cli.missing", what), ExitCodes.Usage);
    }

    public static async Task<int> RunAsync(string[] argv, CliEnvironment env)
    {
        try
        {
            var a = Parse(argv);
            if (a.Positional.Count == 0 || a.Switches.Contains("--help") || a.Switches.Contains("-h"))
            {
                env.Out.WriteLine(Help);
                return a.Positional.Count == 0 && !a.Switches.Contains("--help") && !a.Switches.Contains("-h") ? ExitCodes.Usage : ExitCodes.Ok;
            }
            var command = string.Join(' ', a.Positional.Take(2));
            var store = new CredentialStore(env.ConfigDir);
            switch (a.Positional[0])
            {
                case "login": return await LoginAsync(a, env, store);
                case "logout": return await LogoutAsync(env, store);
                case "manual": env.Out.WriteLine(Manual); return ExitCodes.Ok;
                case "daemon" when a.Positional.ElementAtOrDefault(1) is "install" or "uninstall": return Service(a, env);
            }
            if (command == "daemon") Daemon.CheckOptions(a.Many("--runtime").Count, DaemonEnv(a), a.Opt("--as-user"));
            var api = Connect(env, store);
            return command switch
            {
                "whoami" or "whoami " => Print(env, a, await api.GetAsync<AccountInfo>("api/account/me"), i => $"{i.UserName}{(i.IsAdmin ? " (admin)" : "")}"),
                "sessions list" => Print(env, a, await api.GetAsync<List<SessionDto>>("api/account/sessions"),
                    l => Lines(l, s => $"{s.Id}  {s.Kind,-4} {s.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}{(s.Current ? T("cli.thisSession") : "")}  {s.Device}")),
                "token create" => Print(env, a, await api.PostAsync<ApiTokenDto>("api/account/tokens", new NewApiTokenRequest(a.Rest(2, T("cli.arg.tokenName")))),
                    t => T("cli.tokenCreated", t.Name) + $"\n{t.Token}\n\nClaude Code:\n  claude mcp add --transport http agentweb {api.Server}mcp --header \"Authorization: Bearer {t.Token}\""),
                "2fa status" => Print(env, a, await api.GetAsync<TwoFactorDto>("api/account/2fa"), t => T(t.Enabled ? "cli.2fa.on" : "cli.2fa.off")),
                "2fa setup" => Print(env, a, await api.GetAsync<TwoFactorDto>("api/account/2fa"), t => t.Enabled ? T("cli.2fa.already") : T("cli.2fa.setup", t.SharedKey, t.AuthenticatorUri)),
                "2fa enable" => await Done(env, a, api.PostAsync("api/account/2fa/enable", new TwoFactorCodeRequest(a.At(2, T("cli.arg.code"))))),
                "2fa disable" => await Done(env, a, api.PostAsync("api/account/2fa/disable", new TwoFactorCodeRequest(a.At(2, T("cli.arg.code"))))),
                "sessions revoke" => await Done(env, a, api.PostAsync($"api/account/sessions/{Uri.EscapeDataString(a.At(2, T("cli.arg.sessionId")))}/revoke", null)),

                "channel list" => Print(env, a, await api.GetAsync<List<ChannelDto>>("api/workspace/channels"),
                    l => Lines(l, c => $"{c.Id,4}  {(c.IsPrivate ? "🔒" : "#")} {c.Name}{(c.Unread > 0 ? "  " + (c.Mentions > 0 ? T("cli.unreadMentions", c.Unread, c.Mentions) : T("cli.unread", c.Unread)) : "")}")),
                "channel read" => await MarkReadAsync(env, a, api),
                "channel create" => Print(env, a, await api.PostAsync<ChannelDto>("api/workspace/channels",
                    new NewChannelRequest(a.At(2, T("cli.arg.channelName")), a.Switches.Contains("--private"))), c => $"{c.Id}  {c.Name}"),
                "channel members" => Print(env, a, await api.GetAsync<List<string>>($"api/workspace/channels/{await ChannelAsync(api, a.At(2, T("cli.arg.channel")))}/members"),
                    l => l.Count == 0 ? T("cli.publicChannel") : string.Join('\n', l)),
                "channel invite" => await Done(env, a, api.PostAsync($"api/workspace/channels/{await ChannelAsync(api, a.At(2, T("cli.arg.channel")))}/members",
                    new { name = a.At(3, T("cli.arg.userEmail")) })),

                "message send" => await Done(env, a, api.PostAsync($"api/workspace/channels/{await ChannelAsync(api, a.At(2, T("cli.arg.channel")))}/messages",
                    new NewMessageRequest(a.Rest(3, T("cli.arg.text")), Guid.NewGuid().ToString("N")))),
                "message read" => await ReadMessagesAsync(env, a, api),
                "message reply" => await Done(env, a, api.PostAsync($"api/workspace/channels/{await ChannelAsync(api, a.At(2, T("cli.arg.channel")))}/messages",
                    new NewMessageRequest(a.Rest(4, T("cli.arg.text")), Guid.NewGuid().ToString("N"), Long(a.At(3, T("cli.arg.messageId")))))),
                "message thread" => Print(env, a, await api.GetAsync<List<MessageDto>>(
                    $"api/workspace/channels/{await ChannelAsync(api, a.At(2, T("cli.arg.channel")))}/messages/{Long(a.At(3, T("cli.arg.messageId")))}/thread"), MessageLines),
                "message search" => Print(env, a, await api.GetAsync<List<SearchHitDto>>($"api/workspace/search?q={Uri.EscapeDataString(a.Rest(2, T("cli.arg.searchText")))}"),
                    l => Lines(l, h => $"#{h.ChannelName} [{h.Message.Id}] {h.Message.At.ToLocalTime():yyyy-MM-dd HH:mm} {h.Message.Author}: {h.Message.Text}")),

                "task list" => Print(env, a, await api.GetAsync<List<TaskDto>>($"api/workspace/channels/{await ChannelAsync(api, a.At(2, T("cli.arg.channel")))}/tasks"),
                    l => Lines(l, t => $"#{t.Id,-4} {t.Status,-10} {t.Title}{(t.Assignee is null ? "" : $"  → {(t.AssigneeIsAgent ? "@" : "")}{t.Assignee}")}")),
                "task create" => Print(env, a, await api.PostAsync<TaskDto>($"api/workspace/channels/{await ChannelAsync(api, a.At(2, T("cli.arg.channel")))}/tasks",
                    new NewTaskRequest(a.Rest(3, T("cli.arg.title")), a.Opt("--description") ?? "", a.Opt("--assign") is "me" ? NewTaskRequest.Me : a.Opt("--assign"))),
                    t => $"#{t.Id} {t.Title}"),
                "task done" => await Done(env, a, api.PostAsync($"api/workspace/tasks/{Int(a.At(2, T("cli.arg.taskId")))}/status", new { status = WorkStatus.Done })),
                "task reopen" => await Done(env, a, api.PostAsync($"api/workspace/tasks/{Int(a.At(2, T("cli.arg.taskId")))}/status", new { status = WorkStatus.Todo })),

                "agent list" => Print(env, a, await api.GetAsync<List<string>>("api/workspace/agents"), l => string.Join('\n', l.Select(n => "@" + n))),
                "user create" => await CreateUserAsync(env, a, api),
                "daemon" => await Daemon.RunAsync(api, env, a.Many("--runtime").Select(Daemon.ParseRuntime).ToList(),
                    a.Opt("--machine") ?? Environment.MachineName,
                    DaemonEnv(a),
                    a.Opt("--parallel") is { } p ? Int(p) : 1, a.Opt("--as-user")),
                "daemon status" => Print(env, a, await api.GetAsync<List<MachineDto>>("api/daemon/machines"),
                    l => Lines(l, m => T("cli.machine", m.Machine, string.Join(", ", m.Runtimes), m.Running, m.LastSeen.ToLocalTime().ToString("HH:mm:ss")))),
                _ => throw new CliException(T("cli.unknownCommand", command), ExitCodes.Usage),
            };
        }
        catch (CliException ex)
        {
            env.Err.WriteLine(ex.Message);
            return ex.ExitCode;
        }
        catch (HttpRequestException ex)
        {
            env.Err.WriteLine(T("cli.unreachable", ex.Message));
            return ExitCodes.Error;
        }
    }

    static Args Parse(string[] argv)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>();
        var all = new Dictionary<string, List<string>>(); // opzioni ripetibili (--runtime)
        var switches = new HashSet<string>();
        for (var i = 0; i < argv.Length; i++)
        {
            var t = argv[i];
            if (Flags.Contains(t)) switches.Add(t);
            else if (t.StartsWith("--"))
            {
                if (i + 1 >= argv.Length) throw new CliException(T("cli.optionNeedsValue", t), ExitCodes.Usage);
                options[t] = argv[++i];
                (all.TryGetValue(t, out var l) ? l : all[t] = []).Add(options[t]);
            }
            else positional.Add(t);
        }
        return new(positional, options, switches, all);
    }

    static ApiClient Connect(CliEnvironment env, CredentialStore store)
    {
        var creds = store.Load() ?? throw new CliException(T("cli.notSignedIn"), ExitCodes.Unauthenticated);
        return new ApiClient(Http(env, creds.Server), store, creds);
    }

    static HttpClient Http(CliEnvironment env, string server)
    {
        var http = new HttpClient(env.CreateHandler()) { BaseAddress = new Uri(server.EndsWith('/') ? server : server + "/"), Timeout = TimeSpan.FromSeconds(60) };
        // Riconoscibile nella pagina "Dispositivi" e in `agentweb sessions list`.
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            $"agentweb-cli/{typeof(CliApp).Assembly.GetName().Version?.ToString(3)} ({Environment.MachineName}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription})");
        return http;
    }

    static async Task<int> LoginAsync(Args a, CliEnvironment env, CredentialStore store)
    {
        var server = a.Opt("--server") ?? env.DefaultServer ?? store.Load()?.Server
            ?? throw new CliException(T("cli.needServer"), ExitCodes.Usage);
        var email = a.Opt("--email") ?? throw new CliException(T("cli.missing", "--email"), ExitCodes.Usage);
        if (a.Opt("--password") is not null) throw new CliException(T("cli.noPasswordArg"), ExitCodes.Usage);
        string? password;
        if (a.Switches.Contains("--password-stdin")) password = env.In.ReadLine();
        else if (env.ReadSecret is { } read) { env.Err.Write("Password: "); password = read(); }
        else throw new CliException(T("cli.noTerminal"), ExitCodes.Usage);

        using var http = Http(env, server);
        var r = await http.PostAsJsonAsync("identity/login", new { email, password });
        if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized && (await r.Content.ReadAsStringAsync()).Contains("RequiresTwoFactor"))
        {
            r.Dispose();
            string? code;
            if (a.Switches.Contains("--password-stdin")) code = env.In.ReadLine();
            else if (env.ReadSecret is { } read) { env.Err.Write(T("cli.codePrompt")); code = read(); }
            else code = null;
            if (string.IsNullOrWhiteSpace(code)) throw new CliException(T("cli.codeRequired"), ExitCodes.Unauthenticated);
            r = await http.PostAsJsonAsync("identity/login", new { email, password, twoFactorCode = code.Trim() });
            if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized) { r.Dispose(); throw new CliException(T("auth.badCode"), ExitCodes.Unauthenticated); }
        }
        using var _ = r;
        if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new CliException(T("auth.badCredentials"), ExitCodes.Unauthenticated);
        if (!r.IsSuccessStatusCode) throw new CliException(T("cli.loginFailed", (int)r.StatusCode));
        var t = (await r.Content.ReadFromJsonAsync<JsonElement>())!;
        store.Save(new Credentials(server, email, t.GetProperty("accessToken").GetString()!, t.GetProperty("refreshToken").GetString()!));
        if (a.Json) env.Out.WriteLine(JsonSerializer.Serialize(new { userName = email, server }, CliJson.Options));
        else env.Out.WriteLine(T("cli.signedIn", email, server));
        return ExitCodes.Ok;
    }

    static async Task<int> LogoutAsync(CliEnvironment env, CredentialStore store)
    {
        if (store.Load() is { } creds)
        {
            try
            {
                var api = new ApiClient(Http(env, creds.Server), store, creds);
                var me = await api.GetAsync<AccountInfo>("api/account/me");
                if (me.SessionId is { } sid) await api.PostAsync($"api/account/sessions/{sid}/revoke", null);
            }
            catch (CliException) { /* già scaduta: basta cancellare le credenziali locali */ }
            store.Delete();
        }
        env.Out.WriteLine(T("cli.signedOut"));
        return ExitCodes.Ok;
    }

    static async Task<int> ReadMessagesAsync(CliEnvironment env, Args a, ApiClient api)
    {
        var channel = await ChannelAsync(api, a.At(2, T("cli.arg.channel")));
        List<MessageDto> list;
        if (a.Opt("--since") is { } since)
        {
            var replay = await api.GetAsync<ReplayDto>($"api/workspace/channels/{channel}/messages/since/{Long(since)}");
            if (replay.Reset) throw new CliException(T("cli.tooManySince"));
            list = replay.Messages;
        }
        else list = await api.GetAsync<List<MessageDto>>($"api/workspace/channels/{channel}/messages");
        return Print(env, a, list, MessageLines);
    }

    static string MessageLines(List<MessageDto> l) =>
        Lines(l, m => $"[{m.Id}]{(m.ParentId is { } p ? $" ↳{p}" : "")} {m.At.ToLocalTime():HH:mm} {m.Author}: {m.Text}");

    // Fino all'ultimo messaggio del canale (il cursore sul server avanza soltanto).
    static async Task<int> MarkReadAsync(CliEnvironment env, Args a, ApiClient api)
    {
        var channel = await ChannelAsync(api, a.At(2, T("cli.arg.channel")));
        var last = (await api.GetAsync<List<MessageDto>>($"api/workspace/channels/{channel}/messages")).LastOrDefault()?.Id ?? 0;
        return await Done(env, a, api.PostAsync($"api/workspace/channels/{channel}/read", new MarkReadRequest(last)));
    }

    static List<string> DaemonEnv(Args a) => (a.Opt("--env") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // Scrive (o rimuove) il file di avvio automatico; caricarlo resta un comando esplicito dell'utente.
    static int Service(Args a, CliEnvironment env)
    {
        var keep = new[] { "--runtime", "--machine", "--env", "--parallel", "--as-user" };
        var args = keep.SelectMany(k => a.Many(k).SelectMany(v => new[] { k, v })).ToList();
        if (a.Positional[1] == "install" && a.Many("--runtime").Count == 0) throw new CliException(T("cli.needRuntime"), ExitCodes.Usage);
        foreach (var r in a.Many("--runtime")) Daemon.ParseRuntime(r); // errori di sintassi subito, non al primo avvio
        if (a.Positional[1] == "install") Daemon.CheckOptions(a.Many("--runtime").Count, DaemonEnv(a), a.Opt("--as-user"));
        var (path, content, hint) = Daemon.ServiceFile(env.ExecutablePath, args, Environment.GetEnvironmentVariable("AGENTWEB_CONFIG_DIR"));
        if (a.Positional[1] == "uninstall")
        {
            if (File.Exists(path)) File.Delete(path);
            env.Out.WriteLine(T("cli.serviceRemoved", path, OperatingSystem.IsMacOS() ? $"launchctl unload {path}" : "systemctl --user disable --now agentweb-daemon"));
            return ExitCodes.Ok;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        env.Out.WriteLine(T("cli.serviceWritten", path, hint));
        return ExitCodes.Ok;
    }

    static async Task<int> CreateUserAsync(CliEnvironment env, Args a, ApiClient api)
    {
        var email = a.At(2, T("cli.arg.email"));
        string? password;
        if (a.Switches.Contains("--password-stdin")) password = env.In.ReadLine();
        else if (env.ReadSecret is { } read) { env.Err.Write(T("cli.passwordFor", email)); password = read(); }
        else throw new CliException(T("cli.newUserPassword"), ExitCodes.Usage);
        await api.PostAsync("api/admin/users", new { email, password });
        return Print(env, a, new { userName = email }, _ => T("cli.userCreated", email));
    }

    // <canale> = id numerico o nome (risolto tra i canali che l'utente vede: un privato altrui "non esiste").
    static async Task<int> ChannelAsync(ApiClient api, string idOrName)
    {
        if (int.TryParse(idOrName, out var id)) return id;
        var name = idOrName.TrimStart('#');
        var channels = await api.GetAsync<List<ChannelDto>>("api/workspace/channels");
        return channels.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new CliException(T("cli.channelNotFound", idOrName));
    }

    static int Print<T>(CliEnvironment env, Args a, T value, Func<T, string> human)
    {
        env.Out.WriteLine(a.Json ? JsonSerializer.Serialize(value, CliJson.Options) : human(value));
        return ExitCodes.Ok;
    }

    static async Task<int> Done(CliEnvironment env, Args a, Task action)
    {
        await action;
        env.Out.WriteLine(a.Json ? "{ \"ok\": true }" : T("cli.done"));
        return ExitCodes.Ok;
    }

    static string Lines<TItem>(List<TItem> items, Func<TItem, string> line) => items.Count == 0 ? T("cli.none") : string.Join('\n', items.Select(line));

    static int Int(string s) => int.TryParse(s, out var v) ? v : throw new CliException(T("cli.notANumber", s), ExitCodes.Usage);
    static long Long(string s) => long.TryParse(s, out var v) ? v : throw new CliException(T("cli.notANumber", s), ExitCodes.Usage);
}
