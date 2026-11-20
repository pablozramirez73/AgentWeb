using System.Net;
using System.Net.Http.Json;
using AgentWeb.Shared.Workspace;
using AgentWeb.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWeb.Tests;

// Profilo: nome visualizzato, foto (solo immagini vere), verifica email, accessi esterni (solo se configurati, solo per account collegati).
public sealed class ProfileTests
{
    const string Alice = "alice@test.it";
    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    [Fact]
    public async Task DisplayName_AndPhoto_AreSaved_AndOnlyRealImagesAreAccepted()
    {
        using var app = new TestApp();
        await app.CreateUserAsync(Alice);
        using var http = await app.LoginAsync(Alice);

        Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsJsonAsync("/api/account/profile", new ProfileRequest("  Alice Rossi  "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/account/profile", new ProfileRequest(new string('x', 61)))).StatusCode);

        var svg = "<svg onload=alert(1)>"u8.ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/account/avatar", new AvatarRequest("image/png", Convert.ToBase64String(svg)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/account/avatar", new AvatarRequest("image/png", Convert.ToBase64String(new byte[Profile.MaxAvatarBytes + 1])))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsJsonAsync("/api/account/avatar", new AvatarRequest("image/gif", Convert.ToBase64String(Png)))).StatusCode);

        var p = (await http.GetFromJsonAsync<ProfileDto>("/api/account/profile"))!;
        Assert.Equal("Alice Rossi", p.DisplayName);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), p.Avatar); // il tipo viene dai byte, non dal client
        Assert.False(p.EmailConfirmed);
        Assert.False(p.CanSendEmail);
        Assert.Empty(p.Providers);

        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync("/api/account/avatar")).StatusCode);
        Assert.Null((await http.GetFromJsonAsync<ProfileDto>("/api/account/profile"))!.Avatar);
    }

    [Fact]
    public async Task EmailConfirmation_NeedsSmtp_AndTheLinkConfirmsOnce()
    {
        using var app = new TestApp();
        await app.CreateUserAsync(Alice);
        using var http = await app.LoginAsync(Alice);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/account/email/confirm", null)).StatusCode); // niente SMTP configurato

        string token;
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            token = await users.GenerateEmailConfirmationTokenAsync((await users.FindByNameAsync(Alice))!);
        }
        using var browser = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var bad = await browser.GetAsync($"/account/confirm-email?user={Uri.EscapeDataString(Alice)}&token=sbagliato");
        Assert.Equal("/account?email=invalid", bad.Headers.Location!.ToString());
        var ok = await browser.GetAsync($"/account/confirm-email?user={Uri.EscapeDataString(Alice)}&token={Uri.EscapeDataString(token)}");
        Assert.Equal("/account?email=confirmed", ok.Headers.Location!.ToString());
        Assert.True((await http.GetFromJsonAsync<ProfileDto>("/api/account/profile"))!.EmailConfirmed);
    }

    [Fact]
    public async Task ExternalProviders_ExistOnlyWhenConfigured()
    {
        using (var plain = new TestApp())
        {
            using var anon = plain.CreateClient();
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/account/external/GitHub")).StatusCode);
            Assert.DoesNotContain("Continua con", await anon.GetStringAsync("/login"));
        }

        using var app = new TestApp(new() { ["Auth:GitHub:ClientId"] = "cid", ["Auth:GitHub:ClientSecret"] = "secret" });
        using var browser = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Contains("Continua con GitHub", await browser.GetStringAsync("/login"));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/account/external/Google")).StatusCode);

        var challenge = await browser.GetAsync("/account/external/GitHub");
        var location = challenge.Headers.Location!.ToString();
        Assert.StartsWith("https://github.com/login/oauth/authorize?", location);
        Assert.Contains("client_id=cid", location);
        Assert.Contains("code_challenge=", location); // PKCE
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("http://localhost/signin-github"), location);

        // Senza il cookie esterno (nessun ritorno da GitHub) il callback non fa entrare nessuno.
        var callback = await browser.GetAsync("/account/external-callback?mode=login");
        Assert.StartsWith("/login?error=", callback.Headers.Location!.ToString());

        await app.CreateUserAsync(Alice);
        using var http = await app.LoginAsync(Alice);
        Assert.Equal(["GitHub"], (await http.GetFromJsonAsync<ProfileDto>("/api/account/profile"))!.Providers);
    }
}
