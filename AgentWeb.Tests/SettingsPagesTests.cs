using System.Net;
using System.Text.RegularExpressions;

namespace AgentWeb.Tests;

// Le sezioni delle impostazioni condividono il menu a sinistra e si aprono (prerender) per un utente collegato dal browser.
public sealed class SettingsPagesTests
{
    [Theory]
    [InlineData("/account", "Account", "Cambia password")]
    [InlineData("/appearance", "Aspetto", "Segue il dispositivo")]
    [InlineData("/notifications", "Notifiche", "Qualcuno mi menziona")]
    [InlineData("/devices", "Sicurezza e dispositivi", "Verifica in due passaggi")]
    [InlineData("/settings", "Agenti", "Server MCP")]
    public async Task EverySection_HasTheSettingsMenu(string path, string title, string content)
    {
        using var app = new TestApp();
        await app.CreateUserAsync("alice@test.it", admin: true);
        using var browser = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await (await browser.GetAsync("/login")).Content.ReadAsStringAsync();
        var field = Regex.Match(login, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
        var r = await browser.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [field.Groups[1].Value] = WebUtility.HtmlDecode(field.Groups[2].Value), ["email"] = "alice@test.it", ["password"] = TestApp.Password,
        }));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        var page = await browser.GetAsync(path);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Cerca nelle impostazioni", html);
        Assert.Matches($"class=\"settings-title\"[^>]*>{title}<", html); // [^>]*: l'attributo dell'isolamento CSS
        Assert.Contains(content, html);
    }
}
