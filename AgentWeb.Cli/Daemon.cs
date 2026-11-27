using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentWeb.Shared.Workspace;
using static AgentWeb.Shared.Lang;

namespace AgentWeb.Cli;

// `agentweb daemon`: esegue su questo computer gli agenti che un admin ha legato al tuo utente (LLM "daemon").
// Ogni runtime è un comando che legge il prompt da stdin e scrive la risposta su stdout (es. claude="claude -p").
// Credenziali: il processo figlio NON riceve il token di AgentWeb né le variabili AGENTWEB_*: l'ambiente è ripulito
// e resta solo una lista minima (PATH, HOME, lingua…) più le variabili chieste con --env. Il prompt passa da stdin, mai da argv.
// Revoca: se la sessione del daemon viene revocata, il primo poll o heartbeat riceve 401, i processi figli vengono
// terminati e il daemon esce con codice 3.
// Isolamento vero: con lo stesso utente del sistema il runtime potrebbe leggere ~/.config/agentweb/credentials.json.
// --as-user UTENTE lo avvia come un altro utente (sudo -n -u UTENTE), che quel file non può leggerlo. Serve una regola sudoers
// senza password solo verso quell'utente, es.:  tu ALL=(agentweb-runner) NOPASSWD: ALL
public static class Daemon
{
    public sealed record Runtime(string Name, string File, List<string> Args);

    static readonly string[] SafeEnv = ["PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "LC_ALL", "LC_CTYPE", "TMPDIR", "TERM"];
    const int MaxOutput = 200_000;

    // "nome=comando arg1 arg2". shortcut: argomenti separati da spazi, senza virgolette; per comandi complessi usare uno script.
    public static Runtime ParseRuntime(string spec)
    {
        var eq = spec.IndexOf('=');
        var parts = eq > 0 ? spec[(eq + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        if (eq <= 0 || parts.Length == 0) throw new CliException(T("daemon.badRuntime", spec), ExitCodes.Usage);
        return new(spec[..eq].Trim(), parts[0], parts.Skip(1).ToList());
    }

    public static async Task<int> RunAsync(ApiClient api, CliEnvironment env, List<Runtime> runtimes, string machine, List<string> passEnv, int parallel, string? asUser = null)
    {
        CheckOptions(runtimes.Count, passEnv, asUser);
        var byName = runtimes.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var done = new ConcurrentDictionary<string, DaemonResult>(); // consegne già eseguite: una riconsegna non rilancia il runtime
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(env.Cancel);
        env.Err.WriteLine(T(asUser is null ? "daemon.listening" : "daemon.listeningAs", machine, api.UserName, api.Server, string.Join(", ", byName.Keys), asUser));
        return await RunWorkersAsync(api, env, byName, done, stop, machine, passEnv, parallel, asUser);
    }

    // Errori d'uso prima di qualsiasi richiesta al server (anche da `daemon install`).
    public static void CheckOptions(int runtimes, List<string> passEnv, string? asUser)
    {
        if (runtimes == 0) throw new CliException(T("cli.needRuntime"), ExitCodes.Usage);
        if (asUser is not null && !System.Text.RegularExpressions.Regex.IsMatch(asUser, "^[A-Za-z_][A-Za-z0-9_.-]{0,31}$"))
            throw new CliException(T("daemon.badUser", asUser), ExitCodes.Usage);
        if (asUser is not null && passEnv.Count > 0)
            throw new CliException(T("daemon.envWithUser"), ExitCodes.Usage);
        if (passEnv.FirstOrDefault(n => n.StartsWith("AGENTWEB_", StringComparison.OrdinalIgnoreCase)) is { } bad)
            throw new CliException(T("daemon.noAgentwebVars", bad), ExitCodes.Usage);
    }

    static async Task<int> RunWorkersAsync(ApiClient api, CliEnvironment env, Dictionary<string, Runtime> byName, ConcurrentDictionary<string, DaemonResult> done,
        CancellationTokenSource stop, string machine, List<string> passEnv, int parallel, string? asUser)
    {

        async Task Worker()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpResponseMessage r;
                try { r = await api.SendRawAsync(HttpMethod.Post, "api/daemon/poll", new DaemonPollRequest(machine, [.. byName.Keys]), stop.Token); }
                // Anche il timeout di HttpClient (TaskCanceledException senza stop): è rete lenta, non uno spegnimento.
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !stop.IsCancellationRequested)
                {
                    env.Err.WriteLine(T("daemon.unreachable", ex.Message));
                    await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                    continue;
                }
                using var _ = r;
                if (r.StatusCode == HttpStatusCode.NoContent) continue;
                if (!r.IsSuccessStatusCode)
                {
                    env.Err.WriteLine(T("daemon.httpError", (int)r.StatusCode));
                    await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                    continue;
                }
                var d = (await r.Content.ReadFromJsonAsync<DaemonDelivery>(CliJson.Options, stop.Token))!;
                env.Err.WriteLine($"daemon: → {d.Id[..8]} {d.Runtime}"); // mai il prompt nei log
                if (!done.TryGetValue(d.Id, out var result))
                {
                    result = byName.TryGetValue(d.Runtime, out var rt) ? await ExecuteAsync(api, env, rt, d, passEnv, asUser, stop.Token)
                        : new(null, T("daemon.runtimeMissing", d.Runtime));
                    done[d.Id] = result;
                    if (done.Count > 1000) done.Clear(); // basta ricordare le ultime
                }
                (await api.SendRawAsync(HttpMethod.Post, $"api/daemon/deliveries/{d.Id}/complete", result, stop.Token)).Dispose(); // 404: già scaduta, il server l'ha riassegnata
                env.Err.WriteLine($"daemon: ← {d.Id[..8]} {(result.Error is null ? "ok" : T("daemon.error", result.Error))}");
            }
        }

        var workers = Enumerable.Range(0, Math.Max(1, parallel)).Select(_ => Task.Run(Worker)).ToList();
        try
        {
            await await Task.WhenAny(workers); // il primo errore (es. sessione revocata) ferma tutti
            return ExitCodes.Ok;
        }
        catch (OperationCanceledException) when (env.Cancel.IsCancellationRequested) { return ExitCodes.Ok; }
        finally
        {
            stop.Cancel(); // termina i processi figli ancora attivi
            try { await Task.WhenAll(workers); } catch { /* già riportato dal primo */ }
            env.Err.WriteLine("daemon: fermato");
        }
    }

    // Esegue il runtime con ambiente ripulito; ogni DaemonHeartbeat conferma al server che la consegna è ancora in corso.
    // 404 sull'heartbeat = consegna annullata o riassegnata: il processo viene terminato.
    // Nessuna shell: comando e argomenti passano come lista. Ambiente ripulito; con asUser lo ripulisce anche sudo (env_reset).
    public static ProcessStartInfo StartInfo(Runtime rt, IEnumerable<string> passEnv, string? asUser)
    {
        var psi = new ProcessStartInfo(asUser is null ? rt.File : "/usr/bin/sudo")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (asUser is not null) foreach (var a in new[] { "-n", "-u", asUser, "-H", "--", rt.File }) psi.ArgumentList.Add(a);
        foreach (var a in rt.Args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var name in SafeEnv.Concat(passEnv))
            if (Environment.GetEnvironmentVariable(name) is { } v) psi.Environment[name] = v;
        return psi;
    }

    // Con asUser il daemon può segnalare sudo (tiene il suo uid reale), ma non i processi sotto, che girano come asUser (EPERM).
    // Quindi prima si uccidono quelli, come asUser e dal basso, finché sudo è vivo: morto sudo, finirebbero orfani sotto
    // init/launchd e pgrep -P non li troverebbe più. Poi il tree kill, che senza asUser basta da solo.
    static void Kill(Process process, string? asUser)
    {
        // Già uscito (fine normale) o già rilasciato: niente da fermare, e pgrep -P non troverebbe più i figli.
        try { if (process.HasExited) return; } catch (InvalidOperationException) { return; }
        if (asUser is not null)
        {
            var killer = new ProcessStartInfo("/usr/bin/sudo") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in new[] { "-n", "-u", asUser, "--", "/bin/sh", "-c",
                "t() { for c in $(pgrep -P \"$1\"); do t \"$c\"; done; kill -KILL \"$1\" 2>/dev/null; }; for c in $(pgrep -P \"$0\"); do t \"$c\"; done",
                process.Id.ToString() }) killer.ArgumentList.Add(a);
            try { Process.Start(killer)?.WaitForExit(5000); } catch (Exception) { }
        }
        try { process.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    static async Task<DaemonResult> ExecuteAsync(ApiClient api, CliEnvironment env, Runtime rt, DaemonDelivery d, List<string> passEnv, string? asUser, CancellationToken stop)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var process = new Process { StartInfo = StartInfo(rt, passEnv, asUser) };
        try { process.Start(); }
        catch (Exception ex) { return new(null, T("daemon.cannotStart", rt.File, ex.Message)); }
        // Fuori dal callback: Cancel() esegue le registrazioni in fila, e il kill via sudo può attendere fino a 5 s per processo.
        using var kill = run.Token.Register(() => _ = Task.Run(() => Kill(process, asUser)));

        var stdout = ReadCappedAsync(process.StandardOutput);
        var stderr = ReadCappedAsync(process.StandardError);
        // Un runtime che esce subito (o non legge stdin) chiude la pipe: l'esito lo danno codice d'uscita e stderr.
        try { await process.StandardInput.WriteAsync(d.Prompt); process.StandardInput.Close(); }
        catch (IOException) { }

        var heartbeat = Task.Run(async () =>
        {
            while (!run.IsCancellationRequested)
            {
                await Task.Delay(env.DaemonHeartbeat, run.Token);
                try
                {
                    using var h = await api.SendRawAsync(HttpMethod.Post, $"api/daemon/deliveries/{d.Id}/heartbeat", null, run.Token);
                    if (h.StatusCode == HttpStatusCode.NotFound) return; // annullata o riassegnata
                }
                // Rete assente o lenta per un attimo (anche il timeout di HttpClient): ci pensa il lease se dura troppo.
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !run.IsCancellationRequested) { }
            }
        });

        var exited = process.WaitForExitAsync(CancellationToken.None);
        if (await Task.WhenAny(exited, heartbeat) == heartbeat)
        {
            run.Cancel(); // consegna annullata (404) o sessione revocata (401): il processo si ferma subito
            await exited;
            if (heartbeat.IsFaulted) await heartbeat; // rilancia la revoca (CliException, codice 3)
            stop.ThrowIfCancellationRequested();
            return new(null, T("daemon.cancelled"));
        }
        run.Cancel();
        try { await heartbeat; } catch (OperationCanceledException) { }
        stop.ThrowIfCancellationRequested();
        var (output, error) = (await stdout, await stderr);
        return process.ExitCode == 0 ? new(output.Trim(), null)
            : new(null, T("daemon.exited", rt.Name, process.ExitCode, Tail(error)));
    }

    static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buffer)) > 0)
            if (sb.Length < MaxOutput) sb.Append(buffer, 0, Math.Min(n, MaxOutput - sb.Length)); // oltre il limite si legge e si scarta
        return sb.ToString();
    }

    static string Tail(string s) => s.Length <= 500 ? s.Trim() : "…" + s[^500..].Trim();

    // Avvio automatico al login: launchd (macOS) o systemd utente (Linux), con gli stessi argomenti del daemon.
    public static (string Path, string Content, string Hint) ServiceFile(string exe, IEnumerable<string> daemonArgs, string? configDir)
    {
        var args = new[] { exe, "daemon" }.Concat(daemonArgs).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Il servizio non eredita l'ambiente della shell: profilo e lingua vanno scritti nel file.
        var vars = new List<(string Key, string Value)>();
        if (configDir is not null) vars.Add(("AGENTWEB_CONFIG_DIR", configDir));
        if (Code != Default) vars.Add(("AGENTWEB_LANG", Code));
        if (OperatingSystem.IsMacOS())
        {
            var path = Path.Combine(home, "Library", "LaunchAgents", "it.agentweb.daemon.plist");
            static string X(string s) => System.Security.SecurityElement.Escape(s);
            var env = vars.Count == 0 ? "" : $"<key>EnvironmentVariables</key><dict>{string.Concat(vars.Select(v => $"<key>{v.Key}</key><string>{X(v.Value)}</string>"))}</dict>";
            return (path, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                  <key>Label</key><string>it.agentweb.daemon</string>
                  <key>ProgramArguments</key><array>{string.Concat(args.Select(a => $"<string>{X(a)}</string>"))}</array>
                  {env}
                  <key>RunAtLoad</key><true/><key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
                  <key>StandardErrorPath</key><string>{X(Path.Combine(home, "Library", "Logs", "agentweb-daemon.log"))}</string>
                </dict></plist>
                """, $"launchctl load -w {path}");
        }
        if (OperatingSystem.IsLinux())
        {
            var path = Path.Combine(home, ".config", "systemd", "user", "agentweb-daemon.service");
            static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return (path, $"""
                [Unit]
                Description=AgentWeb daemon
                [Service]
                ExecStart={string.Join(' ', args.Select(Q))}
                {string.Join("\n", vars.Select(v => $"Environment={v.Key}={Q(v.Value)}"))}
                Restart=on-failure
                [Install]
                WantedBy=default.target
                """, "systemctl --user daemon-reload && systemctl --user enable --now agentweb-daemon");
        }
        throw new CliException(T("daemon.serviceUnsupported"), ExitCodes.Usage);
    }
}
