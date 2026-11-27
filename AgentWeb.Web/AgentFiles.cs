using System.Diagnostics;
using System.Text;
using AgentWeb.Shared;
using AgentWeb.Shared.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using static AgentWeb.Shared.Lang;

namespace AgentWeb.Web;

// File degli agenti: ognuno ha la sua cartella sul server (root/<nome>) e vi agisce solo secondo le sue FileRule.
// Gli strumenti compaiono solo se una regola concede quel diritto. Niente percorsi fuori dalla cartella né link simbolici.
// shortcut: un file eseguito gira con l'utente del server (può leggere ciò che legge il server, anche fuori dalla cartella);
// per questo l'esecuzione è spenta finché AgentFiles:AllowExecute non è true. Per isolarla davvero serve un container per esecuzione.
public sealed class AgentFiles(string root, bool allowExecute)
{
    public const int MaxRead = 200_000, MaxWrite = 1_000_000, MaxOutput = 20_000, MaxList = 300;
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    public static string DirFor(string root, string agent) => Path.Combine(root, agent.ToLowerInvariant());

    public IEnumerable<AITool> For(LocalAgentConfig agent)
    {
        if (agent.Files.Count == 0) yield break;
        yield return AIFunctionFactory.Create(() => List(agent), "list_files", T("tool.list_files"));
        if (agent.Files.Any(r => r.Read))
            yield return AIFunctionFactory.Create(([LDescription("tool.file.path")] string path) => Read(agent, path), "read_file", T("tool.read_file"));
        if (agent.Files.Any(r => r.Write))
            yield return AIFunctionFactory.Create(([LDescription("tool.file.path")] string path, [LDescription("tool.write_file.content")] string content) =>
                Write(agent, path, content), "write_file", T("tool.write_file"));
        if (allowExecute && agent.Files.Any(r => r.Execute))
            yield return AIFunctionFactory.Create(([LDescription("tool.file.path")] string path, [LDescription("tool.run_file.args")] string[]? args, CancellationToken ct) =>
                RunAsync(agent, path, args ?? [], ct), "run_file", T("tool.run_file"));
    }

    public string List(LocalAgentConfig agent)
    {
        var dir = Dir(agent);
        var lines = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .Select(rel => (rel, flags: Flags(agent, rel)))
            .Where(x => x.flags.Length > 0)
            .Order().Take(MaxList + 1).Select(x => $"{x.rel} [{x.flags}]").ToList();
        if (lines.Count == 0) return T("files.none", string.Join("; ", agent.Files));
        return string.Join("\n", lines.Take(MaxList)) + (lines.Count > MaxList ? "\n" + T("files.more", MaxList) : "");
    }

    public string Read(LocalAgentConfig agent, string path)
    {
        if (Check(agent, path, r => r.Read, "files.op.read") is { Error: { } e }) return e;
        var full = Resolve(Dir(agent), path)!;
        if (!File.Exists(full)) return T("files.notFound", path);
        var size = new FileInfo(full).Length;
        if (size > MaxRead) return T("files.tooLarge", path, size, MaxRead);
        return T("files.content", path) + "\n" + File.ReadAllText(full);
    }

    public string Write(LocalAgentConfig agent, string path, string content)
    {
        if (Check(agent, path, r => r.Write, "files.op.write") is { Error: { } e }) return e;
        var bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > MaxWrite) return T("files.tooLarge", path, bytes, MaxWrite);
        var full = Resolve(Dir(agent), path)!;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return T("files.written", path, bytes);
    }

    public async Task<string> RunAsync(LocalAgentConfig agent, string path, string[] args, CancellationToken ct)
    {
        if (!allowExecute) return T("files.executeOff");
        if (Check(agent, path, r => r.Execute, "files.op.execute") is { Error: { } e }) return e;
        var dir = Dir(agent);
        var full = Resolve(dir, path)!;
        if (!File.Exists(full)) return T("files.notFound", path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(full, File.GetUnixFileMode(full) | UnixFileMode.UserExecute);

        var psi = new ProcessStartInfo(full) { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Ambiente vuoto: le variabili del server (stringa del database, chiavi) non passano al file eseguito.
        psi.Environment.Clear();
        psi.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        psi.Environment["HOME"] = dir;
        psi.Environment["LANG"] = "C.UTF-8";

        using var p = Process.Start(psi)!;
        var output = new StringBuilder();
        void Add(string? line) { if (line is null) return; lock (output) if (output.Length < MaxOutput) output.AppendLine(line); }
        p.OutputDataReceived += (_, e) => Add(e.Data);
        p.ErrorDataReceived += (_, e) => Add(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RunTimeout);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            return T("files.timeout", path, (int)RunTimeout.TotalSeconds) + "\n" + Cut(output);
        }
        p.WaitForExit(); // svuota gli ultimi eventi di output
        return T("files.exit", p.ExitCode) + "\n" + Cut(output);
    }

    static string Cut(StringBuilder s) { lock (s) return s.Length <= MaxOutput ? s.ToString() : s.ToString(0, MaxOutput) + "…"; }

    string Dir(LocalAgentConfig agent) => Directory.CreateDirectory(Path.GetFullPath(DirFor(root, agent.Name))).FullName.TrimEnd(Path.DirectorySeparatorChar);

    (string? Error, bool Ok) Check(LocalAgentConfig agent, string path, Func<FileRule, bool> right, string op)
    {
        path = path.Trim();
        if (path.Contains('*') || !FileRule.IsValidPath(path) || Resolve(Dir(agent), path) is null) return (T("files.badPath", path), false);
        return Allowed(agent, path, right) ? (null, true) : (T("files.denied", path, T(op)), false);
    }

    static string Flags(LocalAgentConfig agent, string rel) =>
        $"{(Allowed(agent, rel, r => r.Read) ? "r" : "")}{(Allowed(agent, rel, r => r.Write) ? "w" : "")}{(Allowed(agent, rel, r => r.Execute) ? "x" : "")}";

    public static bool Allowed(LocalAgentConfig agent, string rel, Func<FileRule, bool> right) =>
        agent.Files.Where(right).Any(r => Matches(r.Path, rel));

    static bool Matches(string pattern, string rel)
    {
        var m = new Matcher(StringComparison.Ordinal);
        m.AddInclude(pattern);
        if (!pattern.Contains('*')) m.AddInclude(pattern.TrimEnd('/') + "/**"); // una cartella vale per il suo contenuto
        return m.Match(rel).HasMatches;
    }

    // Percorso assoluto dentro dir, o null se esce dalla cartella o passa per un link simbolico (porterebbe fuori).
    static string? Resolve(string dir, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(dir, rel));
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar)) return null;
        for (var p = full; p.Length > dir.Length; p = Path.GetDirectoryName(p)!)
            if (new FileInfo(p).LinkTarget is not null) return null;
        return full;
    }
}
