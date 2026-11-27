using AgentWeb.Shared.Agents;
using AgentWeb.Web;

namespace AgentWeb.Tests;

// Diritti degli agenti sui file della loro cartella: regole per percorso, niente uscite dalla cartella, esecuzione solo se accesa.
public sealed class AgentFilesTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("agentfiles").FullName;
    public void Dispose() => Directory.Delete(root, recursive: true);

    static LocalAgentConfig Agent(string rules) => new() { Name = "Scrittore", Files = FileRule.ParseList(rules) };

    [Fact]
    public void Rules_ParseAndPrint()
    {
        var rules = FileRule.ParseList("docs/**=r; out=rw\nrun.sh=x");
        Assert.Equal(["docs/**=r", "out=rw", "run.sh=x"], rules.Select(r => r.ToString()));
        Assert.Throws<FormatException>(() => FileRule.ParseList("docs/**=rq"));
        Assert.Throws<FormatException>(() => FileRule.ParseList("docs"));
    }

    [Fact]
    public void Write_AndRead_FollowTheRules()
    {
        var files = new AgentFiles(root, allowExecute: false);
        var a = Agent("out=rw; docs/*.md=r");
        Assert.StartsWith("Scritto", files.Write(a, "out/a/b.txt", "ciao"));
        Assert.Contains("ciao", files.Read(a, "out/a/b.txt"));
        Assert.Contains("non hai il diritto", files.Write(a, "docs/x.md", "no"));
        Assert.Contains("non hai il diritto", files.Read(a, "secret.txt"));
        Assert.Contains("out/a/b.txt [rw]", files.List(a));
    }

    [Fact]
    public void Paths_CannotLeaveTheFolder()
    {
        var files = new AgentFiles(root, allowExecute: false);
        var a = Agent("**=rw");
        File.WriteAllText(Path.Combine(root, "outside.txt"), "segreto");
        Assert.Contains("non valido", files.Read(a, "../outside.txt"));
        Assert.Contains("non valido", files.Read(a, "/etc/passwd"));
        Directory.CreateDirectory(AgentFiles.DirFor(root, a.Name));
        File.CreateSymbolicLink(Path.Combine(AgentFiles.DirFor(root, a.Name), "link"), root);
        Assert.Contains("non valido", files.Read(a, "link/outside.txt"));
    }

    [Fact]
    public async Task Run_NeedsTheRight_AndTheServerSwitch()
    {
        if (OperatingSystem.IsWindows()) return;
        var a = Agent("run.sh=rwx; other.sh=rw");
        var off = new AgentFiles(root, allowExecute: false);
        Assert.DoesNotContain(off.For(a), t => t.Name == "run_file");
        Assert.Contains("spenta", await off.RunAsync(a, "run.sh", [], default));

        var on = new AgentFiles(root, allowExecute: true);
        on.Write(a, "run.sh", "#!/bin/sh\necho \"ciao $1\"\necho \"db=$AGENTWEB_TEST_SECRET\"\n");
        Environment.SetEnvironmentVariable("AGENTWEB_TEST_SECRET", "segreto");
        string result;
        try { result = await on.RunAsync(a, "run.sh", ["mondo"], default); }
        finally { Environment.SetEnvironmentVariable("AGENTWEB_TEST_SECRET", null); }
        Assert.Contains("Codice di uscita 0", result);
        Assert.Contains("ciao mondo", result);
        Assert.DoesNotContain("segreto", result); // l'ambiente del server non passa al file eseguito
        on.Write(a, "other.sh", "#!/bin/sh\necho no\n");
        Assert.Contains("non hai il diritto", await on.RunAsync(a, "other.sh", [], default));
    }

    [Fact]
    public void Validate_ChecksRules()
    {
        var s = new AgentSettings();
        s.LocalAgents[0].Files = [new() { Path = "../x", Read = true }, new() { Path = "ok" }, new() { Path = "run.sh", Execute = true }];
        var errors = s.Validate(allowStdio: false);
        Assert.Contains(errors, e => e.Contains("../x"));
        Assert.Contains(errors, e => e.Contains("non concede niente"));
        Assert.Contains(errors, e => e.Contains("AgentFiles:AllowExecute"));
        Assert.DoesNotContain(s.Validate(allowStdio: false, allowExecute: true), e => e.Contains("AgentFiles:AllowExecute"));
    }
}
