using System.Net.Http.Json;
using System.Xml;
using AgentWeb.Web;

namespace AgentWeb.Tests;

// Icone degli agenti: SVG disegnati dall'LLM, accettati solo se innocui; altrimenti l'icona dal nome.
public sealed class AgentIconsTests
{
    [Fact]
    public void Clean_ExtractsTheSvgFromTheReply()
    {
        var svg = AgentIcons.Clean("Ecco l'icona:\n```svg\n<svg viewBox=\"0 0 128 128\"><rect width=\"128\" height=\"128\" fill=\"red\"/></svg>\n```");
        Assert.NotNull(svg);
        Assert.StartsWith("<svg", svg);
        Assert.Contains("http://www.w3.org/2000/svg", svg); // senza xmlns il browser non lo mostra come immagine
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://evil.example/x.png\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"javascript:alert(1)\"><rect/></a></svg>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg>&x;</svg>")]
    [InlineData("<svg><rect></svg>")]
    [InlineData("nessun disegno")]
    public void Clean_RejectsUnsafeOrBrokenSvg(string reply) => Assert.Null(AgentIcons.Clean(reply));

    [Fact]
    public void Fallback_IsValidStableAndDiffersByName()
    {
        var a = AgentIcons.Fallback("Critico");
        new XmlDocument().LoadXml(a);
        Assert.Equal(a, AgentIcons.Fallback("critico"));
        Assert.NotEqual(a, AgentIcons.Fallback("Architetto"));
        Assert.NotNull(AgentIcons.Clean(a)); // passa lo stesso filtro delle icone disegnate
    }

    [Fact]
    public async Task Endpoint_GivesEveryEnabledAgentAnIcon()
    {
        using var app = new TestApp();
        await app.CreateUserAsync("alice@test.it");
        var http = await app.LoginAsync("alice@test.it");
        var icons = await http.GetFromJsonAsync<Dictionary<string, string>>("/api/workspace/agents/icons");
        Assert.Equal(["Architetto", "Critico"], icons!.Keys.Order());
        Assert.All(icons.Values, s => Assert.StartsWith("<svg", s));
    }
}
