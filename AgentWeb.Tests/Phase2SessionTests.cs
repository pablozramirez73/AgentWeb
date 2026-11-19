using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AgentWeb.Shared.Workspace;
using AgentWeb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWeb.Tests;

// Fase 2 — autenticazione e sessioni: revoca immediata, refresh legato alla sessione, niente self-signup, rate limit.
public sealed class Phase2SessionTests
{
    const string Alice = "alice@test.it";

    [Fact]
    public async Task Token_StopsWorking_AsSoonAsItsSessionIsRevoked()
    {
        using var app = new TestApp();
        await app.CreateUserAsync(Alice);
        using var http = await app.LoginAsync(Alice);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/workspace/channels")).StatusCode);

        var sessions = await http.GetFromJsonAsync<List<SessionDto>>("/api/account/sessions");
        var current = Assert.Single(sessions!, s => s.Current);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync($"/api/account/sessions/{current.Id}/revoke", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/workspace/channels")).StatusCode);
    }

    [Fact]
    public async Task Refresh_KeepsTheSession_AndFailsOnceItIsRevoked()
    {
        using var app = new TestApp();
        await app.CreateUserAsync(Alice);
        var tokens = await app.LoginTokensAsync(Alice);
        using var anon = app.CreateClient();

        var refreshed = await anon.PostAsJsonAsync("/identity/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var fresh = (await refreshed.Content.ReadFromJsonAsync<TestApp.Tokens>())!;
        using (var http = app.Bearer(fresh.AccessToken)) // il token rinnovato porta ancora il claim "sid"
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/workspace/channels")).StatusCode);

        using (var http = app.Bearer(fresh.AccessToken))
            await http.PostAsync("/api/account/sessions/revoke-all", null);
        var afterRevoke = await anon.PostAsJsonAsync("/identity/refresh", new { refreshToken = fresh.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task WebLogout_KillsTheCookie_EvenIfSomeoneCopiedIt()
    {
        using var app = new TestApp();
        await app.CreateUserAsync(Alice);
        using var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        // Pagina di login -> token antiforgery + cookie, poi il form.
        var page = await browser.GetAsync("/login");
        var antiforgeryCookie = Cookies(page);
        var html = await page.Content.ReadAsStringAsync();
        var field = Regex.Match(html, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
        var login = new HttpRequestMessage(HttpMethod.Post, "/account/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [field.Groups[1].Value] = WebUtility.HtmlDecode(field.Groups[2].Value), ["email"] = Alice, ["password"] = TestApp.Password,
            }),
        };
        login.Headers.Add("Cookie", antiforgeryCookie);
        var loggedIn = await browser.SendAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, loggedIn.StatusCode);
        var session = Cookies(loggedIn);

        Assert.Equal(HttpStatusCode.OK, (await Get(browser, "/api/workspace/channels", session)).StatusCode);
        await Get(browser, "/logout", session);
        // Il cookie copiato prima del logout non vale più: la sessione è revocata nel DB, non solo cancellata dal browser.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(browser, "/api/workspace/channels", session)).StatusCode);
    }

    [Fact]
    public async Task OnlyTheFirstAccount_CanSelfRegister_AndBecomesAdmin()
    {
        using var app = new TestApp();
        using var http = app.CreateClient();

        var first = await http.PostAsJsonAsync("/account/register", new { email = "capo@test.it", password = TestApp.Password });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            Assert.True(await users.IsInRoleAsync((await users.FindByNameAsync("capo@test.it"))!, AccountEndpoints.AdminRole));
        }

        var second = await http.PostAsJsonAsync("/account/register", new { email = "intruso@test.it", password = TestApp.Password });
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        // Anche il vecchio endpoint di Identity non esiste più.
        var identity = await http.PostAsJsonAsync("/identity/register", new { email = "intruso@test.it", password = TestApp.Password });
        Assert.False(identity.IsSuccessStatusCode);
    }

    [Fact]
    public async Task OnlyAdmins_CreateAccounts()
    {
        using var app = new TestApp();
        await app.CreateUserAsync("capo@test.it", admin: true);
        await app.CreateUserAsync(Alice);
        using var admin = await app.LoginAsync("capo@test.it");
        using var alice = await app.LoginAsync(Alice);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await alice.PostAsJsonAsync("/api/admin/users", new { email = "nuovo@test.it", password = TestApp.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await admin.PostAsJsonAsync("/api/admin/users", new { email = "nuovo@test.it", password = TestApp.Password })).StatusCode);
        await app.LoginTokensAsync("nuovo@test.it");
    }

    [Fact]
    public async Task AdminRevokesAllSessionsOfAUser()
    {
        using var app = new TestApp();
        await app.CreateUserAsync("capo@test.it", admin: true);
        await app.CreateUserAsync(Alice);
        using var admin = await app.LoginAsync("capo@test.it");
        using var phone = await app.LoginAsync(Alice);
        using var laptop = await app.LoginAsync(Alice);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/users/{Alice}/revoke-sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/workspace/channels")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/workspace/channels")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/workspace/channels")).StatusCode);
    }

    [Fact]
    public async Task Login_IsRateLimitedPerIp()
    {
        using var app = new TestApp(new() { ["Auth:RateLimitPerMinute"] = "3" });
        using var http = app.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            codes.Add((await http.PostAsJsonAsync("/identity/login", new { email = "nessuno@test.it", password = "x" })).StatusCode);
        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], codes);
    }

    static string Cookies(HttpResponseMessage r) =>
        string.Join("; ", r.Headers.TryGetValues("Set-Cookie", out var v) ? v.Select(c => c.Split(';')[0]) : []);

    static Task<HttpResponseMessage> Get(HttpClient http, string url, string cookies)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Cookie", cookies);
        return http.SendAsync(req);
    }
}
