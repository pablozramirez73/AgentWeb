using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using AgentWeb.Shared.Workspace;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;

namespace AgentWeb.Web;

// Profilo dell'utente (nome visualizzato, foto), verifica dell'email e accessi esterni (Google, GitHub).
// Gli accessi esterni servono solo a entrare in un account GIÀ esistente e collegato da lì: non creano account (niente self-signup).
public static class Profile
{
    public const int MaxAvatarBytes = 256 * 1024;

    public static async Task<ProfileDto> GetAsync(UserManager<AppUser> users, Mailer mailer, IAuthenticationSchemeProvider schemes, AppUser user) => new(
        user.UserName!, user.DisplayName,
        user.Avatar is { } a ? $"data:{user.AvatarType};base64,{Convert.ToBase64String(a)}" : null,
        user.EmailConfirmed, mailer.Configured,
        (await users.GetLoginsAsync(user)).Select(l => new ExternalLoginDto(l.LoginProvider, l.ProviderDisplayName)).ToList(),
        (await schemes.GetAllSchemesAsync()).Where(s => ExternalProviders.Names.Contains(s.Name)).Select(s => s.Name).ToList());

    public static async Task SaveAsync(UserManager<AppUser> users, AppUser user, string? displayName)
    {
        displayName = displayName?.Trim();
        if (displayName?.Length > 60) throw new ArgumentException("Nome visualizzato: al massimo 60 caratteri.");
        user.DisplayName = string.IsNullOrEmpty(displayName) ? null : displayName;
        Check(await users.UpdateAsync(user));
    }

    public static async Task SetAvatarAsync(UserManager<AppUser> users, AppUser user, string? contentType, byte[]? data)
    {
        if (data is null) { user.Avatar = null; user.AvatarType = null; }
        else
        {
            if (data.Length is 0 or > MaxAvatarBytes) throw new ArgumentException("La foto deve pesare al massimo 256 KB.");
            // Il tipo dichiarato non basta: si guardano i primi byte (niente SVG o HTML mascherati da immagine).
            user.AvatarType = SniffImage(data) ?? throw new ArgumentException("Formato non supportato: usa PNG, JPEG o WebP.");
            user.Avatar = data;
        }
        Check(await users.UpdateAsync(user));
    }

    internal static string? SniffImage(ReadOnlySpan<byte> d) =>
        d.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? "image/png"
        : d.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? "image/jpeg"
        : d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    public static async Task SendConfirmationAsync(UserManager<AppUser> users, Mailer mailer, AppUser user, Uri baseUri, CancellationToken ct = default)
    {
        if (!mailer.Configured) throw new InvalidOperationException("Invio email non configurato sul server.");
        if (user.EmailConfirmed) return;
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var link = new Uri(baseUri, $"account/confirm-email?user={Uri.EscapeDataString(user.UserName!)}&token={Uri.EscapeDataString(token)}");
        await mailer.SendAsync(user.Email!, "Conferma la tua email su AgentWeb",
            $"Per confermare l'indirizzo {user.Email} apri questo link:\n\n{link}\n\nSe non sei stato tu, ignora questo messaggio.", ct);
    }

    public static async Task RemoveLoginAsync(UserManager<AppUser> users, AppUser user, string provider)
    {
        foreach (var l in (await users.GetLoginsAsync(user)).Where(l => l.LoginProvider == provider))
            Check(await users.RemoveLoginAsync(user, l.LoginProvider, l.ProviderKey));
    }

    static void Check(IdentityResult r)
    {
        if (!r.Succeeded) throw new InvalidOperationException(string.Join(" ", r.Errors.Select(e => e.Description)));
    }

    public static void MapProfileEndpoints(this WebApplication app)
    {
        var me = app.MapGroup("/api/account").RequireAuthorization();
        me.MapGet("/profile", async (ClaimsPrincipal u, UserManager<AppUser> users, Mailer mailer, IAuthenticationSchemeProvider schemes) =>
            await users.GetUserAsync(u) is { } user ? Results.Ok(await GetAsync(users, mailer, schemes, user)) : Results.Unauthorized());
        me.MapPut("/profile", (ProfileRequest req, ClaimsPrincipal u, UserManager<AppUser> users) =>
            Run(users, u, user => SaveAsync(users, user, req.DisplayName)));
        me.MapPut("/avatar", (AvatarRequest req, ClaimsPrincipal u, UserManager<AppUser> users) =>
            Run(users, u, user => SetAvatarAsync(users, user, req.ContentType, Base64(req.Base64))));
        me.MapDelete("/avatar", (ClaimsPrincipal u, UserManager<AppUser> users) => Run(users, u, user => SetAvatarAsync(users, user, null, null)));
        me.MapDelete("/logins/{provider}", (string provider, ClaimsPrincipal u, UserManager<AppUser> users) =>
            Run(users, u, user => RemoveLoginAsync(users, user, provider)));
        me.MapPost("/email/confirm", (ClaimsPrincipal u, UserManager<AppUser> users, Mailer mailer, HttpContext http) =>
            Run(users, u, user => SendConfirmationAsync(users, mailer, user, BaseUri(http), http.RequestAborted)))
            .RequireRateLimiting(SessionEndpoints.AuthRateLimit); // manda email: non deve diventare uno spammer

        // Link dell'email: funziona anche da un altro dispositivo, il token è la prova.
        app.MapGet("/account/confirm-email", async (string user, string token, UserManager<AppUser> users) =>
        {
            var ok = await users.FindByNameAsync(user) is { } u && (await users.ConfirmEmailAsync(u, token)).Succeeded;
            return Results.Redirect(ok ? "/account?email=confirmed" : "/account?email=invalid");
        }).AllowAnonymous();

        ExternalProviders.Map(app);
    }

    static async Task<IResult> Run(UserManager<AppUser> users, ClaimsPrincipal u, Func<AppUser, Task> action)
    {
        if (await users.GetUserAsync(u) is not { } user) return Results.Unauthorized();
        try { await action(user); return Results.NoContent(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { return Results.Text(ex.Message, statusCode: 400); }
    }

    static byte[] Base64(string s) => s.Length > MaxAvatarBytes * 4 / 3 + 4 ? throw new ArgumentException("La foto deve pesare al massimo 256 KB.") : Convert.FromBase64String(s);

    internal static Uri BaseUri(HttpContext http) => new($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/");
}

// Invio email via SMTP. Configurazione: Email:Smtp:Host, Email:Smtp:Port (587), Email:Smtp:User, Email:Smtp:Password, Email:From.
public sealed class Mailer(IConfiguration config)
{
    public bool Configured => config["Email:Smtp:Host"] is { Length: > 0 } && config["Email:From"] is { Length: > 0 };

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        using var smtp = new System.Net.Mail.SmtpClient(config["Email:Smtp:Host"], config.GetValue("Email:Smtp:Port", 587)) { EnableSsl = config.GetValue("Email:Smtp:Ssl", true) };
        if (config["Email:Smtp:User"] is { Length: > 0 } user) smtp.Credentials = new System.Net.NetworkCredential(user, config["Email:Smtp:Password"]);
        using var msg = new System.Net.Mail.MailMessage(config["Email:From"]!, to, subject, body);
        await smtp.SendMailAsync(msg, ct);
    }
}

// Accesso con Google / GitHub (OAuth 2.0 con PKCE). Si attiva solo il provider configurato:
//   Auth:Google:ClientId / ClientSecret   (redirect URI da registrare: https://<dominio>/signin-google)
//   Auth:GitHub:ClientId / ClientSecret   (callback URL: https://<dominio>/signin-github)
//   Auth:Apple:ClientId (Services ID) / TeamId / KeyId / PrivateKey (contenuto del .p8)   (return URL: https://<dominio>/signin-apple)
public static class ExternalProviders
{
    public static readonly string[] Names = ["Google", "GitHub", "Apple"];

    public static void AddExternalProviders(this WebApplicationBuilder builder)
    {
        var auth = builder.Services.AddAuthentication();
        Add(auth, builder.Configuration, "Google", "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token",
            "https://openidconnect.googleapis.com/v1/userinfo", ["openid", "email", "profile"], id: "sub", nameKey: "name");
        Add(auth, builder.Configuration, "GitHub", "https://github.com/login/oauth/authorize", "https://github.com/login/oauth/access_token",
            "https://api.github.com/user", ["read:user", "user:email"], id: "id", nameKey: "login");
        AddApple(auth, builder.Configuration);
    }

    // Apple: OpenID Connect con risposta in POST (form_post) e niente userinfo; l'identità (sub, email) è nell'id_token.
    static void AddApple(AuthenticationBuilder auth, IConfiguration config)
    {
        if (config["Auth:Apple:ClientId"] is not { Length: > 0 } clientId) return;
        string teamId = config["Auth:Apple:TeamId"] ?? "", keyId = config["Auth:Apple:KeyId"] ?? "", key = config["Auth:Apple:PrivateKey"] ?? "";
        if (teamId.Length == 0 || keyId.Length == 0 || key.Length == 0)
            throw new InvalidOperationException("Accesso con Apple: servono Auth:Apple:TeamId, KeyId e PrivateKey oltre a ClientId.");
        auth.AddOpenIdConnect("Apple", "Apple", o =>
        {
            o.SignInScheme = IdentityConstants.ExternalScheme;
            o.Authority = "https://appleid.apple.com";
            o.ClientId = clientId;
            o.CallbackPath = "/signin-apple";
            o.ResponseType = "code";
            o.ResponseMode = "form_post"; // obbligatorio per Apple quando si chiede l'email
            o.UsePkce = false; // shortcut: Apple non documenta il code_verifier; bastano client secret, state e nonce
            o.GetClaimsFromUserInfoEndpoint = false;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("email");
            o.Events.OnAuthorizationCodeReceived = ctx =>
            {
                ctx.TokenEndpointRequest!.ClientSecret = AppleClientSecret(clientId, teamId, keyId, key, DateTime.UtcNow);
                return Task.CompletedTask;
            };
        });
    }

    // Il client secret di Apple è un JWT ES256 firmato con la chiave .p8; lo rigeneriamo a ogni login (scade dopo 5 minuti).
    public static string AppleClientSecret(string clientId, string teamId, string keyId, string pem, DateTime now)
    {
        using var ec = System.Security.Cryptography.ECDsa.Create();
        ec.ImportFromPem(pem.Replace("\\n", "\n")); // nelle variabili d'ambiente gli a capo arrivano spesso come \n
        var signing = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.ECDsaSecurityKey(ec) { KeyId = keyId }, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.EcdsaSha256)
            { CryptoProviderFactory = new() { CacheSignatureProviders = false } }; // la chiave viene liberata a fine metodo
        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = teamId, Audience = "https://appleid.apple.com", IssuedAt = now, NotBefore = now, Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = clientId }, SigningCredentials = signing,
        });
    }

    static void Add(AuthenticationBuilder auth, IConfiguration config, string name, string authorize, string token, string userInfo, string[] scopes, string id, string nameKey)
    {
        if (config[$"Auth:{name}:ClientId"] is not { Length: > 0 } clientId) return;
        auth.AddOAuth(name, name, o =>
        {
            o.SignInScheme = IdentityConstants.ExternalScheme;
            o.ClientId = clientId;
            o.ClientSecret = config[$"Auth:{name}:ClientSecret"] ?? "";
            o.CallbackPath = "/signin-" + name.ToLowerInvariant();
            o.AuthorizationEndpoint = authorize;
            o.TokenEndpoint = token;
            o.UserInformationEndpoint = userInfo;
            o.UsePkce = true;
            foreach (var s in scopes) o.Scope.Add(s);
            o.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, id);
            o.ClaimActions.MapJsonKey(ClaimTypes.Name, nameKey);
            o.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
            o.Events.OnCreatingTicket = async ctx =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
                req.Headers.UserAgent.ParseAdd("AgentWeb"); // GitHub rifiuta le richieste senza User-Agent
                req.Headers.Accept.ParseAdd("application/json");
                using var r = await ctx.Backchannel.SendAsync(req, ctx.HttpContext.RequestAborted);
                r.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted));
                ctx.RunClaimActions(json.RootElement);
            };
        });
    }

    internal static void Map(WebApplication app)
    {
        // mode=link: collega il provider all'account con cui si è entrati; altrimenti è un accesso.
        app.MapGet("/account/external/{provider}", async (string provider, string? mode, SignInManager<AppUser> signIn,
            UserManager<AppUser> users, IAuthenticationSchemeProvider schemes, ClaimsPrincipal u) =>
        {
            if (!Names.Contains(provider) || await schemes.GetSchemeAsync(provider) is null) return Results.NotFound();
            var link = mode == "link";
            string? userId = null;
            if (link)
            {
                if (await users.GetUserAsync(u) is not { } user) return Results.Redirect("/login");
                userId = user.Id;
            }
            var props = signIn.ConfigureExternalAuthenticationProperties(provider, $"/account/external-callback?mode={(link ? "link" : "login")}", userId);
            return Results.Challenge(props, [provider]);
        }).AllowAnonymous();

        app.MapGet("/account/external-callback", async (string? mode, SignInManager<AppUser> signIn, UserManager<AppUser> users,
            SessionStore sessions, HttpContext http, ClaimsPrincipal u) =>
        {
            var link = mode == "link";
            var current = link ? await users.GetUserAsync(u) : null;
            var info = await signIn.GetExternalLoginInfoAsync(current?.Id); // userId: protegge il collegamento da un CSRF
            await http.SignOutAsync(IdentityConstants.ExternalScheme);
            if (info is null) return Results.Redirect(link ? "/account?login=failed" : "/login?error=" + Uri.EscapeDataString("Accesso esterno non riuscito."));

            if (link)
            {
                if (current is null) return Results.Redirect("/login");
                // Nel profilo si vede con quale account del provider è collegato (email o nome utente).
                info.ProviderDisplayName = info.Principal.FindFirstValue(ClaimTypes.Email) ?? info.Principal.Identity?.Name ?? info.LoginProvider;
                var r = await users.AddLoginAsync(current, info);
                return Results.Redirect(r.Succeeded ? "/account?login=linked" : "/account?login=taken"); // già collegato a un altro account
            }

            var user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (user is null)
                return Results.Redirect("/login?error=" + Uri.EscapeDataString($"Nessun account collegato a questo account {info.LoginProvider}: entra con email e password e collegalo da Impostazioni → Account."));
            // Con la verifica in due passaggi attiva il provider esterno non basta: serve il codice, quindi il login con password.
            if (await users.GetTwoFactorEnabledAsync(user))
                return Results.Redirect("/login?error=" + Uri.EscapeDataString("Il tuo account ha la verifica in due passaggi: entra con email, password e codice."));
            if (await users.IsLockedOutAsync(user)) return Results.Redirect("/login?error=" + Uri.EscapeDataString("Account bloccato: riprova più tardi."));
            await AccountEndpoints.SignInWebAsync(user, signIn, sessions, http);
            return Results.LocalRedirect("~/");
        }).AllowAnonymous();
    }
}
