using System.ComponentModel;
using System.Text.Json;

namespace AgentWeb.Shared;

// i18n: i testi stanno in i18n/<lingua>.json (chiave → testo, {0} {1} per i valori). Una lingua per installazione:
// Language=en in configurazione sul server (default it); browser e app la chiedono a GET /language; la CLI legge AGENTWEB_LANG.
// Nuova lingua: aggiungere i18n/xx.json con tutte le chiavi (I18nTests lo verifica).
public static class Lang
{
    public const string Default = "it";
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> All = Load();
    static IReadOnlyDictionary<string, string> current = All[Default];

    public static string Code { get; private set; } = Default;

    // Lingua sconosciuta o assente: resta quella attuale.
    public static void Use(string? code)
    {
        if (code is not null && All.TryGetValue(code.Trim().ToLowerInvariant(), out var texts)) { current = texts; Code = code.Trim().ToLowerInvariant(); }
    }

    // Chiave mancante nella lingua attiva: italiano; mancante anche lì: la chiave stessa (si vede subito cosa manca).
    public static string T(string key) => current.TryGetValue(key, out var s) || All[Default].TryGetValue(key, out s) ? s : key;
    public static string T(string key, params object?[] args) => string.Format(T(key), args);

    // Lo stesso testo in tutte le lingue: per riconoscere messaggi salvati prima di un cambio di lingua.
    public static IEnumerable<string> Every(string key) => All.Values.Select(t => t.TryGetValue(key, out var s) ? s : All[Default][key]).Distinct();

    static Dictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var asm = typeof(Lang).Assembly;
        var all = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("i18n.") && n.EndsWith(".json")))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            all[name["i18n.".Length..^".json".Length]] = JsonSerializer.Deserialize<Dictionary<string, string>>(s)!;
        }
        return all;
    }
}

// Per [Description] di tool MCP e funzioni degli agenti: gli attributi vogliono costanti, il testo si legge dalla chiave.
[AttributeUsage(AttributeTargets.All)]
public sealed class LDescriptionAttribute(string key) : DescriptionAttribute
{
    public override string Description => Lang.T(key);
}
