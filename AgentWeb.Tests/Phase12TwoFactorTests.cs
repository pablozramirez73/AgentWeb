using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AgentWeb.Cli;
using AgentWeb.Shared.Workspace;

namespace AgentWeb.Tests;

// Fase 12 — verifica in due passaggi (TOTP): dopo l'attivazione nessun login (app, CLI) passa con la sola password.
public sealed class Phase12TwoFactorTests : IAsyncLifetime
{
    readonly TestApp app = new();
    HttpClient alice = null!;
    string key = null!;

    public async Task InitializeAsync()
    {
        await app.CreateUserAsync("alice@test.it");
        alice = await app.LoginAsync("alice@test.it");
        var status = (await alice.GetFromJsonAsync<TwoFactorDto>("/api/account/2fa"))!;
        Assert.False(status.Enabled);
        key = status.SharedKey!;
        Assert.Contains(key, status.AuthenticatorUri);
    }

    public Task DisposeAsync() { app.Dispose(); return Task.CompletedTask; }

    // RFC 6238: HMAC-SHA1 sul numero di intervalli di 30 s, 6 cifre.
    static string Totp(string base32Key, int offset = 0)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32Key.TrimEnd('=').ToUpperInvariant().Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var secret = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + offset);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(secret, counter);
        var o = hash[^1] & 0x0F;
        var value = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (value % 1_000_000).ToString("D6");
    }

    Task<HttpResponseMessage> AppLogin(string? code) =>
        app.CreateClient().PostAsJsonAsync("/identity/login", new { email = "alice@test.it", password = TestApp.Password, twoFactorCode = code });

    async Task EnableAsync() =>
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsJsonAsync("/api/account/2fa/enable", new TwoFactorCodeRequest(Totp(key)))).StatusCode);

    [Fact]
    public async Task Enabling_NeedsAValidCode_ThenPasswordAloneIsNotEnough()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/account/2fa/enable", new TwoFactorCodeRequest("000000"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AppLogin(null)).StatusCode); // non ancora attiva

        await EnableAsync();
        Assert.True((await alice.GetFromJsonAsync<TwoFactorDto>("/api/account/2fa"))!.Enabled);
        Assert.Null((await alice.GetFromJsonAsync<TwoFactorDto>("/api/account/2fa"))!.SharedKey); // la chiave non si rilegge più

        var withoutCode = await AppLogin(null);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutCode.StatusCode);
        Assert.Contains("RequiresTwoFactor", await withoutCode.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppLogin("123456" == Totp(key) ? "654321" : "123456")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AppLogin(Totp(key))).StatusCode);
    }

    [Fact]
    public async Task Disabling_NeedsAValidCode()
    {
        await EnableAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/account/2fa/disable", new TwoFactorCodeRequest("not-a-code"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppLogin(null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsJsonAsync("/api/account/2fa/disable", new TwoFactorCodeRequest(Totp(key)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AppLogin(null)).StatusCode);
        Assert.NotEqual(key, (await alice.GetFromJsonAsync<TwoFactorDto>("/api/account/2fa"))!.SharedKey); // chiave nuova per la prossima volta
    }

    // Form di /login: senza codice torna al login con l'errore, con il codice giusto entra (redirect a "/").
    async Task<string> WebLogin(string? code)
    {
        using var browser = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var page = await browser.GetAsync("/login");
        var field = System.Text.RegularExpressions.Regex.Match(await page.Content.ReadAsStringAsync(), "name=\"([^\"]+)\" value=\"([^\"]+)\"");
        var form = new Dictionary<string, string>
        {
            [field.Groups[1].Value] = WebUtility.HtmlDecode(field.Groups[2].Value), ["email"] = "alice@test.it", ["password"] = TestApp.Password,
        };
        if (code is not null) form["code"] = code;
        var req = new HttpRequestMessage(HttpMethod.Post, "/account/login") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Add("Cookie", string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
        return (await browser.SendAsync(req)).Headers.Location!.ToString();
    }

    [Fact]
    public async Task WebForm_AsksForTheCode()
    {
        await EnableAsync();
        Assert.StartsWith("/login?error=", await WebLogin(null));
        Assert.StartsWith("/login?error=", await WebLogin("000000"));
        Assert.Equal("/", await WebLogin(Totp(key)));
    }

    [Fact]
    public async Task Cli_ReadsTheCode_FromTheLineAfterThePassword()
    {
        await EnableAsync();
        async Task<int> Login(string stdin) => await CliApp.RunAsync(["login", "--email", "alice@test.it", "--password-stdin"], new CliEnvironment
        {
            In = new StringReader(stdin), Out = new StringWriter(), Err = new StringWriter(),
            ConfigDir = Path.Combine(app.Dir, "cli"), CreateHandler = () => app.Server.CreateHandler(), DefaultServer = app.Server.BaseAddress.ToString(),
        });
        Assert.Equal(ExitCodes.Unauthenticated, await Login(TestApp.Password + "\n"));
        Assert.Equal(ExitCodes.Unauthenticated, await Login(TestApp.Password + "\n000000\n"));
        Assert.Equal(ExitCodes.Ok, await Login(TestApp.Password + "\n" + Totp(key) + "\n"));
    }
}
