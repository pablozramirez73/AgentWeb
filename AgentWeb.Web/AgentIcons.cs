using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using AgentWeb.Shared.Agents;
using Microsoft.Extensions.AI;

using static AgentWeb.Shared.Lang;

namespace AgentWeb.Web;

// Icona quadrata di ogni agente, disegnata dal suo stesso LLM la prima volta che serve (agente appena creato o mai disegnato):
// bella ma satirica sul suo scopo. Finché non è pronta, o se l'LLM non sa farla, c'è un'icona generata dal nome.
// Le icone sono SVG mostrati solo come <img src="data:..."> (niente script) e stanno accanto alle impostazioni, fuori dal JSON.
public sealed class AgentIcons(IConfiguration config, IWebHostEnvironment env, LlmClients llms, ILogger<AgentIcons> log)
{
    const int MaxSvg = 40_000;
    readonly string dir = Path.Combine(Path.GetDirectoryName(config["AgentSettings:Path"] ?? Path.Combine(env.ContentRootPath, "agentsettings.json"))!, "agent-icons");
    readonly ConcurrentDictionary<string, Task> drawing = new(StringComparer.OrdinalIgnoreCase); // un disegno per agente per processo

    // Nome -> SVG per gli agenti indicati; per quelli senza icona parte il disegno in background.
    public Dictionary<string, string> For(IEnumerable<LocalAgentConfig> agents)
    {
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in agents)
        {
            var file = FileFor(a.Name);
            if (File.Exists(file)) { icons[a.Name] = File.ReadAllText(file); continue; }
            icons[a.Name] = Fallback(a.Name);
            drawing.GetOrAdd(a.Name, _ => Task.Run(() => DrawAsync(a, file)));
        }
        return icons;
    }

    string FileFor(string name) => Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..16] + ".svg");

    async Task DrawAsync(LocalAgentConfig agent, string file)
    {
        string svg;
        try
        {
            if (agent.Llm.Kind == LlmKinds.Daemon) throw new InvalidOperationException(T("icon.daemon"));
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var r = await llms.For(agent).GetResponseAsync([new(ChatRole.User, Prompt(agent))], new ChatOptions { MaxOutputTokens = 8192 }, cts.Token);
            svg = Clean(r.Text) ?? throw new InvalidOperationException(T("icon.noSvg"));
        }
        catch (Exception ex)
        {
            log.LogInformation("Icona di {Agent} dal nome: {Reason}", agent.Name, ex.Message);
            svg = Fallback(agent.Name);
        }
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(file + ".tmp", svg);
        File.Move(file + ".tmp", file, overwrite: true);
    }

    static string Prompt(LocalAgentConfig a) => T("icon.prompt", a.Name, a.Prompt);

    // Estrae l'<svg> dalla risposta e lo accetta solo se è XML valido, con radice svg e senza script né riferimenti esterni.
    public static string? Clean(string text)
    {
        int start = text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase), end = text.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < start) return null;
        var svg = text[start..(end + 6)];
        if (svg.Length > MaxSvg) return null;
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                doc.Load(reader);
            if (doc.DocumentElement?.LocalName != "svg") return null;
            foreach (XmlElement el in doc.GetElementsByTagName("*"))
            {
                if (el.LocalName is "script" or "foreignObject" or "image" or "iframe") return null;
                foreach (XmlAttribute at in el.Attributes)
                    if (at.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) || at.LocalName == "href" && !at.Value.StartsWith('#')) return null;
            }
            if (!doc.DocumentElement.HasAttribute("xmlns")) doc.DocumentElement.SetAttribute("xmlns", "http://www.w3.org/2000/svg");
            return doc.DocumentElement.OuterXml;
        }
        catch (XmlException) { return null; }
    }

    // Senza LLM: gradiente e simbolo scelti dal nome (sempre gli stessi per lo stesso agente), con le iniziali.
    public static string Fallback(string name)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
        int hue = h[0] * 360 / 256, hue2 = (hue + 40 + h[1] % 80) % 360;
        string[] marks = ["☕", "🔥", "🙄", "🧠", "📎", "🪄", "🐌", "🤡", "🎩", "🫠"];
        var initials = new string(name.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
        return $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="hsl({hue},85%,58%)"/><stop offset="1" stop-color="hsl({hue2},80%,38%)"/></linearGradient></defs><rect width="128" height="128" fill="url(#g)"/><text x="64" y="78" font-family="system-ui,sans-serif" font-size="46" font-weight="800" fill="#fff" text-anchor="middle">{XmlEscape(initials)}</text><text x="100" y="114" font-size="30" text-anchor="middle">{marks[h[2] % marks.Length]}</text></svg>""";
    }

    static string XmlEscape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;");
}
