using System.Text.RegularExpressions;
using AgentWeb.Shared;
using AgentWeb.Shared.Workspace;

namespace AgentWeb.Tests;

// i18n: ogni lingua in AgentWeb.Shared/i18n ha tutte le chiavi, con gli stessi segnaposto, e ogni chiave usata nel codice esiste.
public sealed class I18nTests
{
    [Fact]
    public void ThereAreAtLeastItalianAndEnglish() => Assert.Superset(new HashSet<string> { "it", "en" }, Lang.All.Keys.ToHashSet());

    [Fact]
    public void EveryLanguage_HasTheSameKeys()
    {
        var reference = Lang.All[Lang.Default].Keys.ToHashSet();
        foreach (var (code, texts) in Lang.All)
        {
            Assert.True(reference.SetEquals(texts.Keys),
                $"{code}: mancano [{string.Join(", ", reference.Except(texts.Keys))}], in più [{string.Join(", ", texts.Keys.Except(reference))}]");
        }
    }

    [Fact]
    public void EveryLanguage_HasTheSamePlaceholders()
    {
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Distinct().Order());
        var bad = (from l in Lang.All
                   from kv in l.Value
                   where Holes(kv.Value) != Holes(Lang.All[Lang.Default][kv.Key])
                   select $"{l.Key}:{kv.Key}").ToList();
        Assert.Empty(bad);
    }

    // Chiavi composte a runtime ("task.status." + stato): ogni valore dell'enum deve avere il suo testo.
    [Fact]
    public void StatusKeys_CoverEveryStatus()
    {
        var keys = Lang.All[Lang.Default];
        foreach (var s in Enum.GetValues<WorkStatus>())
        {
            Assert.Contains($"stats.status.{s}", keys.Keys);
            if (s != WorkStatus.Backlog) Assert.Contains($"task.status.{s}", keys.Keys);
        }
    }

    // Ogni stringa letterale passata come argomento che ha la forma di una chiave (prefisso noto + punto) deve esistere nei file di lingua.
    [Fact]
    public void EveryKeyUsedInCode_Exists()
    {
        var keys = Lang.All[Lang.Default];
        var prefixes = keys.Keys.Select(k => k.Split('.')[0]).ToHashSet();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "AgentWeb.Shared"))) root = root.Parent;
        Assert.NotNull(root);

        // Solo come argomento (dopo "(", ",", "?" o ":"): esclude gli attributi Razor come @bind="llm.Url".
        var literal = new Regex(@"[(,?:]\s*""([a-zA-Z0-9]+(?:\.[a-zA-Z0-9]+)+)""");
        var missing = new List<string>();
        foreach (var dir in new[] { "AgentWeb.Shared", "AgentWeb.Web", "AgentWeb.Cli", "AgentWeb.Web.Client", "AgentWeb" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, dir), "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs") || f.EndsWith(".razor"))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        foreach (Match m in literal.Matches(File.ReadAllText(file)))
        {
            var k = m.Groups[1].Value;
            if (prefixes.Contains(k.Split('.')[0]) && !keys.ContainsKey(k)) missing.Add($"{Path.GetFileName(file)}: {k}");
        }
        Assert.Empty(missing);
    }

    // Niente Lang.Use qui: è stato globale e gli altri test girano in parallelo in italiano.
    [Fact]
    public void English_IsActuallyEnglish() => Assert.Equal("Channels", Lang.All["en"]["nav.channels"]);
}
