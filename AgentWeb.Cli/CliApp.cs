using System.Net.Http.Json;
using System.Text.Json;
using AgentWeb.Shared.Workspace;

namespace AgentWeb.Cli;

// `agentweb` — la stessa CLI per persone e agenti. Usa solo le API pubbliche del server con il token dell'utente:
// nessuna rotta riservata, stessa autorizzazione di web e app (ChannelVisibility, ruoli, sessioni revocabili).
public static class CliApp
{
    const string Help = """
        agentweb — workspace di persone e agenti da terminale

        Accesso
          login [--server URL] --email EMAIL [--password-stdin]   la password non si passa mai come argomento
          logout                                                   revoca la sessione sul server
          whoami
          sessions list | sessions revoke <id>
          token create <nome>                                      token API per agenti esterni (MCP su /mcp)

        Canali e messaggi (<canale> = id o nome)
          channel list
          channel create <nome> [--private]
          channel members <canale>
          channel invite <canale> <email>
          message send <canale> <testo...>
          message read <canale> [--since <id>]
          message reply <canale> <id> <testo...>                    risponde nel thread del messaggio
          message thread <canale> <id>
          message search <testo...>                                 in tutti i canali che vedi
          channel read <canale>                                     segna tutto come letto

        Task
          task list <canale>
          task create <canale> <titolo...> [--description TESTO] [--assign AGENTE|me]
          task done <id> | task reopen <id>

        Daemon (agenti che girano su questo computer: un admin li lega al tuo utente con LLM "daemon")
          daemon --runtime NOME=COMANDO [--runtime …] [--machine NOME] [--env VAR,VAR] [--parallel N]
                 ogni runtime legge il prompt da stdin e scrive la risposta su stdout, es. --runtime claude="claude -p"
          daemon status                                            i tuoi daemon collegati
          daemon install <stesse opzioni> | daemon uninstall        avvio automatico (launchd/systemd utente)

        Altro
          agent list
          user create <email> [--password-stdin]                   solo amministratori

        Opzioni globali: --json (output JSON stabile per script e agenti), --help
        Codici di uscita: 0 ok, 1 errore, 2 uso sbagliato, 3 non autenticato
        Ambiente: AGENTWEB_SERVER (server di default), AGENTWEB_CONFIG_DIR (dove salvare le credenziali)
        """;

    static readonly HashSet<string> Flags = ["--json", "--private", "--password-stdin", "--help", "-h"];

    sealed record Args(List<string> Positional, Dictionary<string, string> Options, HashSet<string> Switches, Dictionary<string, List<string>> All)
    {
        public List<string> Many(string name) => All.GetValueOrDefault(name) ?? [];
        public bool Json => Switches.Contains("--json");
        public string? Opt(string name) => Options.GetValueOrDefault(name);
        public string At(int i, string what) => i < Positional.Count ? Positional[i] : throw new CliException($"Manca {what}.", ExitCodes.Usage);
        public string Rest(int from, string what) => Positional.Count > from ? string.Join(' ', Positional.Skip(from)) : throw new CliException($"Manca {what}.", ExitCodes.Usage);
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
                case "daemon" when a.Positional.ElementAtOrDefault(1) is "install" or "uninstall": return Service(a, env);
            }
            var api = Connect(env, store);
            return command switch
            {
                "whoami" or "whoami " => Print(env, a, await api.GetAsync<AccountInfo>("api/account/me"), i => $"{i.UserName}{(i.IsAdmin ? " (admin)" : "")}"),
                "sessions list" => Print(env, a, await api.GetAsync<List<SessionDto>>("api/account/sessions"),
                    l => Lines(l, s => $"{s.Id}  {s.Kind,-4} {s.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}{(s.Current ? "  (questa)" : "")}  {s.Device}")),
                "token create" => Print(env, a, await api.PostAsync<ApiTokenDto>("api/account/tokens", new NewApiTokenRequest(a.Rest(2, "il nome del token"))),
                    t => $"Token «{t.Name}» (copialo ora, non verrà più mostrato):\n{t.Token}\n\nClaude Code:\n  claude mcp add --transport http agentweb {api.Server}mcp --header \"Authorization: Bearer {t.Token}\""),
                "sessions revoke" => await Done(env, a, api.PostAsync($"api/account/sessions/{Uri.EscapeDataString(a.At(2, "l'id della sessione"))}/revoke", null)),

                "channel list" => Print(env, a, await api.GetAsync<List<ChannelDto>>("api/workspace/channels"),
                    l => Lines(l, c => $"{c.Id,4}  {(c.IsPrivate ? "🔒" : "#")} {c.Name}{(c.Unread > 0 ? $"  ({c.Unread} non letti{(c.Mentions > 0 ? $", {c.Mentions} menzioni" : "")})" : "")}")),
                "channel read" => await MarkReadAsync(env, a, api),
                "channel create" => Print(env, a, await api.PostAsync<ChannelDto>("api/workspace/channels",
                    new NewChannelRequest(a.At(2, "il nome del canale"), a.Switches.Contains("--private"))), c => $"{c.Id}  {c.Name}"),
                "channel members" => Print(env, a, await api.GetAsync<List<string>>($"api/workspace/channels/{await ChannelAsync(api, a.At(2, "il canale"))}/members"),
                    l => l.Count == 0 ? "(canale pubblico: lo vedono tutti)" : string.Join('\n', l)),
                "channel invite" => await Done(env, a, api.PostAsync($"api/workspace/channels/{await ChannelAsync(api, a.At(2, "il canale"))}/members",
                    new { name = a.At(3, "l'email dell'utente") })),

                "message send" => await Done(env, a, api.PostAsync($"api/workspace/channels/{await ChannelAsync(api, a.At(2, "il canale"))}/messages",
                    new NewMessageRequest(a.Rest(3, "il testo"), Guid.NewGuid().ToString("N")))),
                "message read" => await ReadMessagesAsync(env, a, api),
                "message reply" => await Done(env, a, api.PostAsync($"api/workspace/channels/{await ChannelAsync(api, a.At(2, "il canale"))}/messages",
                    new NewMessageRequest(a.Rest(4, "il testo"), Guid.NewGuid().ToString("N"), Long(a.At(3, "l'id del messaggio"))))),
                "message thread" => Print(env, a, await api.GetAsync<List<MessageDto>>(
                    $"api/workspace/channels/{await ChannelAsync(api, a.At(2, "il canale"))}/messages/{Long(a.At(3, "l'id del messaggio"))}/thread"), MessageLines),
                "message search" => Print(env, a, await api.GetAsync<List<SearchHitDto>>($"api/workspace/search?q={Uri.EscapeDataString(a.Rest(2, "il testo da cercare"))}"),
                    l => Lines(l, h => $"#{h.ChannelName} [{h.Message.Id}] {h.Message.At.ToLocalTime():yyyy-MM-dd HH:mm} {h.Message.Author}: {h.Message.Text}")),

                "task list" => Print(env, a, await api.GetAsync<List<TaskDto>>($"api/workspace/channels/{await ChannelAsync(api, a.At(2, "il canale"))}/tasks"),
                    l => Lines(l, t => $"#{t.Id,-4} {t.Status,-10} {t.Title}{(t.Assignee is null ? "" : $"  → {(t.AssigneeIsAgent ? "@" : "")}{t.Assignee}")}")),
                "task create" => Print(env, a, await api.PostAsync<TaskDto>($"api/workspace/channels/{await ChannelAsync(api, a.At(2, "il canale"))}/tasks",
                    new NewTaskRequest(a.Rest(3, "il titolo"), a.Opt("--description") ?? "", a.Opt("--assign") is "me" ? NewTaskRequest.Me : a.Opt("--assign"))),
                    t => $"#{t.Id} {t.Title}"),
                "task done" => await Done(env, a, api.PostAsync($"api/workspace/tasks/{Int(a.At(2, "l'id del task"))}/status", new { status = WorkStatus.Done })),
                "task reopen" => await Done(env, a, api.PostAsync($"api/workspace/tasks/{Int(a.At(2, "l'id del task"))}/status", new { status = WorkStatus.Todo })),

                "agent list" => Print(env, a, await api.GetAsync<List<string>>("api/workspace/agents"), l => string.Join('\n', l.Select(n => "@" + n))),
                "user create" => await CreateUserAsync(env, a, api),
                "daemon" => await Daemon.RunAsync(api, env, a.Many("--runtime").Select(Daemon.ParseRuntime).ToList(),
                    a.Opt("--machine") ?? Environment.MachineName,
                    (a.Opt("--env") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    a.Opt("--parallel") is { } p ? Int(p) : 1),
                "daemon status" => Print(env, a, await api.GetAsync<List<MachineDto>>("api/daemon/machines"),
                    l => Lines(l, m => $"{m.Machine}  runtime: {string.Join(", ", m.Runtimes)}  in corso: {m.Running}  visto {m.LastSeen.ToLocalTime():HH:mm:ss}")),
                _ => throw new CliException($"Comando sconosciuto: {command}. Usa `agentweb --help`.", ExitCodes.Usage),
            };
        }
        catch (CliException ex)
        {
            env.Err.WriteLine(ex.Message);
            return ex.ExitCode;
        }
        catch (HttpRequestException ex)
        {
            env.Err.WriteLine($"Server non raggiungibile: {ex.Message}");
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
                if (i + 1 >= argv.Length) throw new CliException($"L'opzione {t} vuole un valore.", ExitCodes.Usage);
                options[t] = argv[++i];
                (all.TryGetValue(t, out var l) ? l : all[t] = []).Add(options[t]);
            }
            else positional.Add(t);
        }
        return new(positional, options, switches, all);
    }

    static ApiClient Connect(CliEnvironment env, CredentialStore store)
    {
        var creds = store.Load() ?? throw new CliException("Non sei autenticato: esegui `agentweb login`.", ExitCodes.Unauthenticated);
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
            ?? throw new CliException("Indica il server: --server http://host:porta (o AGENTWEB_SERVER).", ExitCodes.Usage);
        var email = a.Opt("--email") ?? throw new CliException("Manca --email.", ExitCodes.Usage);
        if (a.Opt("--password") is not null) throw new CliException("La password non si passa come argomento (resta nella cronologia e nella lista processi): usa --password-stdin o il prompt.", ExitCodes.Usage);
        string? password;
        if (a.Switches.Contains("--password-stdin")) password = env.In.ReadLine();
        else if (env.ReadSecret is { } read) { env.Err.Write("Password: "); password = read(); }
        else throw new CliException("Nessun terminale per chiedere la password: usa --password-stdin.", ExitCodes.Usage);

        using var http = Http(env, server);
        using var r = await http.PostAsJsonAsync("identity/login", new { email, password });
        if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new CliException("Email o password errate.", ExitCodes.Unauthenticated);
        if (!r.IsSuccessStatusCode) throw new CliException($"Login fallito: HTTP {(int)r.StatusCode}.");
        var t = (await r.Content.ReadFromJsonAsync<JsonElement>())!;
        store.Save(new Credentials(server, email, t.GetProperty("accessToken").GetString()!, t.GetProperty("refreshToken").GetString()!));
        if (a.Json) env.Out.WriteLine(JsonSerializer.Serialize(new { userName = email, server }, CliJson.Options));
        else env.Out.WriteLine($"Accesso eseguito come {email} su {server}.");
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
        env.Out.WriteLine("Uscito.");
        return ExitCodes.Ok;
    }

    static async Task<int> ReadMessagesAsync(CliEnvironment env, Args a, ApiClient api)
    {
        var channel = await ChannelAsync(api, a.At(2, "il canale"));
        List<MessageDto> list;
        if (a.Opt("--since") is { } since)
        {
            var replay = await api.GetAsync<ReplayDto>($"api/workspace/channels/{channel}/messages/since/{Long(since)}");
            if (replay.Reset) throw new CliException("Troppi messaggi da recuperare: rileggi senza --since.");
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
        var channel = await ChannelAsync(api, a.At(2, "il canale"));
        var last = (await api.GetAsync<List<MessageDto>>($"api/workspace/channels/{channel}/messages")).LastOrDefault()?.Id ?? 0;
        return await Done(env, a, api.PostAsync($"api/workspace/channels/{channel}/read", new MarkReadRequest(last)));
    }

    // Scrive (o rimuove) il file di avvio automatico; caricarlo resta un comando esplicito dell'utente.
    static int Service(Args a, CliEnvironment env)
    {
        var keep = new[] { "--runtime", "--machine", "--env", "--parallel" };
        var args = keep.SelectMany(k => a.Many(k).SelectMany(v => new[] { k, v })).ToList();
        if (a.Positional[1] == "install" && a.Many("--runtime").Count == 0) throw new CliException("Indica almeno un --runtime nome=comando.", ExitCodes.Usage);
        foreach (var r in a.Many("--runtime")) Daemon.ParseRuntime(r); // errori di sintassi subito, non al primo avvio
        var (path, content, hint) = Daemon.ServiceFile(env.ExecutablePath, args, Environment.GetEnvironmentVariable("AGENTWEB_CONFIG_DIR"));
        if (a.Positional[1] == "uninstall")
        {
            if (File.Exists(path)) File.Delete(path);
            env.Out.WriteLine($"Rimosso {path}. Ferma il servizio con: {(OperatingSystem.IsMacOS() ? $"launchctl unload {path}" : "systemctl --user disable --now agentweb-daemon")}");
            return ExitCodes.Ok;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        env.Out.WriteLine($"Scritto {path}.\nAvvialo con: {hint}");
        return ExitCodes.Ok;
    }

    static async Task<int> CreateUserAsync(CliEnvironment env, Args a, ApiClient api)
    {
        var email = a.At(2, "l'email");
        string? password;
        if (a.Switches.Contains("--password-stdin")) password = env.In.ReadLine();
        else if (env.ReadSecret is { } read) { env.Err.Write($"Password per {email}: "); password = read(); }
        else throw new CliException("Usa --password-stdin per la password del nuovo utente.", ExitCodes.Usage);
        await api.PostAsync("api/admin/users", new { email, password });
        return Print(env, a, new { userName = email }, _ => $"Creato {email}.");
    }

    // <canale> = id numerico o nome (risolto tra i canali che l'utente vede: un privato altrui "non esiste").
    static async Task<int> ChannelAsync(ApiClient api, string idOrName)
    {
        if (int.TryParse(idOrName, out var id)) return id;
        var name = idOrName.TrimStart('#');
        var channels = await api.GetAsync<List<ChannelDto>>("api/workspace/channels");
        return channels.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new CliException($"Canale \"{idOrName}\" inesistente.");
    }

    static int Print<T>(CliEnvironment env, Args a, T value, Func<T, string> human)
    {
        env.Out.WriteLine(a.Json ? JsonSerializer.Serialize(value, CliJson.Options) : human(value));
        return ExitCodes.Ok;
    }

    static async Task<int> Done(CliEnvironment env, Args a, Task action)
    {
        await action;
        env.Out.WriteLine(a.Json ? "{ \"ok\": true }" : "Fatto.");
        return ExitCodes.Ok;
    }

    static string Lines<T>(List<T> items, Func<T, string> line) => items.Count == 0 ? "(nessuno)" : string.Join('\n', items.Select(line));

    static int Int(string s) => int.TryParse(s, out var v) ? v : throw new CliException($"\"{s}\" non è un numero.", ExitCodes.Usage);
    static long Long(string s) => long.TryParse(s, out var v) ? v : throw new CliException($"\"{s}\" non è un numero.", ExitCodes.Usage);
}
