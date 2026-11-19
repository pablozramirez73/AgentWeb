using System.Collections.Concurrent;
using System.Security.Claims;
using AgentWeb.Shared.Workspace;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentWeb.Web;

// Ogni login (cookie web o token app) è una sessione nel DB e il suo id viaggia nel claim "sid".
// Invariante: una sessione revocata non autentica più nulla, dalla richiesta successiva, su HTTP e SignalR.
public sealed class SessionStore(IDbContextFactory<AppDb> dbf, SessionConnections connections)
{
    public const string Claim = "sid";

    public async Task<string> CreateAsync(string userName, string kind, HttpContext http)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = new UserSessionEntity
        {
            Id = Guid.NewGuid().ToString("N"), UserName = userName, Kind = kind, CreatedAt = DateTimeOffset.UtcNow,
            Device = http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 200)] : null,
        };
        db.UserSessions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    // Token API per agenti esterni (Claude, Hermes… via MCP): "awt_<id>_<segreto>". Nel DB solo l'hash del segreto;
    // il token in chiaro si vede una volta sola. È una sessione di tipo "token": compare in Dispositivi e si revoca allo stesso modo.
    public const string TokenPrefix = "awt_";

    public async Task<ApiTokenDto> CreateTokenAsync(string userName, string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 100) throw new ArgumentException("Nome del token: da 1 a 100 caratteri.");
        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using var db = await dbf.CreateDbContextAsync();
        var s = new UserSessionEntity
        {
            Id = Guid.NewGuid().ToString("N"), UserName = userName, Kind = "token", Device = name,
            SecretHash = Hash(secret), CreatedAt = DateTimeOffset.UtcNow,
        };
        db.UserSessions.Add(s);
        await db.SaveChangesAsync();
        return new ApiTokenDto(s.Id, name, $"{TokenPrefix}{s.Id}_{secret}");
    }

    // (utente, sid) se il token è valido e la sua sessione non è revocata; confronto dell'hash a tempo costante.
    public async Task<(string UserName, string Sid)?> ValidateTokenAsync(string token)
    {
        var parts = token.StartsWith(TokenPrefix) ? token[TokenPrefix.Length..].Split('_', 2) : [];
        if (parts.Length != 2) return null;
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.UserSessions.FirstOrDefaultAsync(x => x.Id == parts[0] && x.Kind == "token" && x.RevokedAt == null);
        if (s?.SecretHash is null) return null;
        var ok = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(s.SecretHash), System.Text.Encoding.ASCII.GetBytes(Hash(parts[1])));
        return ok ? (s.UserName, s.Id) : null;
    }

    static string Hash(string secret) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    // shortcut: una query per richiesta, senza cache, così la revoca è immediata anche con più repliche.
    public async Task<bool> IsActiveAsync(string? sid, string? userName)
    {
        if (sid is null || userName is null) return false;
        await using var db = await dbf.CreateDbContextAsync();
        return await db.UserSessions.AnyAsync(s => s.Id == sid && s.UserName == userName && s.RevokedAt == null);
    }

    public async Task<List<SessionDto>> ListAsync(string userName, string? currentSid)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return (await db.UserSessions.Where(s => s.UserName == userName && s.RevokedAt == null).ToListAsync())
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new SessionDto(s.Id, s.Kind, s.Device, s.CreatedAt, s.Id == currentSid)).ToList();
    }

    // userName null = qualsiasi utente (uso amministrativo). Restituisce false se la sessione non esiste o non è sua.
    public async Task<bool> RevokeAsync(string sid, string? userName)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var n = await db.UserSessions.Where(s => s.Id == sid && s.RevokedAt == null && (userName == null || s.UserName == userName))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, DateTimeOffset.UtcNow));
        connections.Abort(sid);
        return n > 0;
    }

    // except: la sessione da lasciare aperta; logins = solo web/app (i token MCP restano validi).
    public async Task<int> RevokeAllAsync(string userName, string? except = null, bool logins = false)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var ids = await db.UserSessions.Where(s => s.UserName == userName && s.RevokedAt == null && s.Id != except && (!logins || s.Kind != "token"))
            .Select(s => s.Id).ToListAsync();
        await db.UserSessions.Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, DateTimeOffset.UtcNow));
        foreach (var id in ids) connections.Abort(id);
        return ids.Count;
    }
}


// La sessione si verifica DENTRO l'autenticazione di ogni schema, così una sessione revocata è un'autenticazione
// fallita: 401 sulle API e su SignalR, redirect al login sulle pagine (non 403, che il client non saprebbe gestire).
public static class SessionValidation
{
    // Cookie web: dopo il controllo del security stamp di Identity.
    public static void ForCookie(CookieAuthenticationOptions o)
    {
        var identityCheck = o.Events.OnValidatePrincipal;
        o.Events.OnValidatePrincipal = async ctx =>
        {
            await identityCheck(ctx);
            if (ctx.Principal is not { } p) return;
            var sessions = ctx.HttpContext.RequestServices.GetRequiredService<SessionStore>();
            if (await sessions.IsActiveAsync(p.FindFirstValue(SessionStore.Claim), p.Identity?.Name)) return;
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        };
    }

    // Token dell'app: il bearer handler non ha un evento dopo la decodifica, quindi il token si decodifica qui e,
    // se la sessione non è attiva, l'autenticazione fallisce prima ancora di costruire il principal.
    public static void ForBearer(BearerTokenOptions o)
    {
        o.Events.OnMessageReceived = async ctx =>
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return;
            if (ctx.Options.BearerTokenProtector.Unprotect(header["Bearer ".Length..].Trim()) is not { } ticket) return; // token non valido: lo rifiuta il handler
            var sessions = ctx.HttpContext.RequestServices.GetRequiredService<SessionStore>();
            if (!await sessions.IsActiveAsync(ticket.Principal.FindFirstValue(SessionStore.Claim), ticket.Principal.Identity?.Name))
                ctx.Fail("Sessione revocata o scaduta.");
        };
    }
}

// Schema "ApiToken": Authorization: Bearer awt_... Il principal è lo stesso di un login normale (ruoli compresi) più il "sid",
// quindi le regole di accesso (ChannelVisibility, ruoli) valgono identiche. Usato solo dall'endpoint MCP.
public sealed class ApiTokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder, SessionStore sessions, UserManager<AppUser> users, SignInManager<AppUser> signIn)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "ApiToken";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer " + SessionStore.TokenPrefix, StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        if (await sessions.ValidateTokenAsync(header["Bearer ".Length..].Trim()) is not { } t) return AuthenticateResult.Fail("Token non valido o revocato.");
        if (await users.FindByNameAsync(t.UserName) is not { } user) return AuthenticateResult.Fail("Utente inesistente.");
        var principal = await signIn.CreateUserPrincipalAsync(user);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new(SessionStore.Claim, t.Sid));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme));
    }
}

// Connessioni SignalR aperte per sessione: la revoca le chiude subito invece di aspettare la prossima richiesta.
public sealed class SessionConnections
{
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, HubCallerContext>> bySession = new();

    public void Add(string sid, HubCallerContext ctx) => bySession.GetOrAdd(sid, _ => new())[ctx.ConnectionId] = ctx;

    public void Remove(string sid, string connectionId)
    {
        if (bySession.TryGetValue(sid, out var set)) set.TryRemove(connectionId, out _);
    }

    // shortcut: solo le connessioni di questa replica; con più repliche le altre si chiudono alla riconnessione (negotiate -> 401).
    public void Abort(string sid)
    {
        if (bySession.TryRemove(sid, out var set))
            foreach (var ctx in set.Values) ctx.Abort();
    }
}

// Circuiti Blazor Server (pagine InteractiveServer): ricontrolla la sessione ogni 10 s e li sgancia se revocata.
public sealed class SessionRevalidatingAuthStateProvider(ILoggerFactory logger, IServiceScopeFactory scopes)
    : RevalidatingServerAuthenticationStateProvider(logger)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(10);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var user = state.User;
        return await scope.ServiceProvider.GetRequiredService<SessionStore>().IsActiveAsync(user.FindFirstValue(SessionStore.Claim), user.Identity?.Name);
    }
}

public static class SessionEndpoints
{
    public const string AuthRateLimit = "auth";

    // /identity/login e /identity/refresh: stessi percorsi e stesso formato di risposta di MapIdentityApi (li usa l'app MAUI),
    // ma il token porta il claim "sid" e il refresh di una sessione revocata fallisce.
    public static void MapSessionEndpoints(this WebApplication app)
    {
        var identity = app.MapGroup("/identity").AllowAnonymous().RequireRateLimiting(AuthRateLimit);

        identity.MapPost("/login", async (LoginRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn, SessionStore sessions, HttpContext http) =>
        {
            var user = await users.FindByNameAsync(req.Email?.Trim() ?? "");
            if (user is null || !(await signIn.CheckPasswordSignInAsync(user, req.Password ?? "", lockoutOnFailure: true)).Succeeded)
                return Results.Unauthorized();
            var principal = await signIn.CreateUserPrincipalAsync(user);
            ((ClaimsIdentity)principal.Identity!).AddClaim(new(SessionStore.Claim, await sessions.CreateAsync(user.UserName!, "app", http)));
            return Results.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme); // scrive AccessTokenResponse
        });

        identity.MapPost("/refresh", async (RefreshRequest req, IOptionsMonitor<BearerTokenOptions> options, TimeProvider time,
            SignInManager<AppUser> signIn, SessionStore sessions) =>
        {
            var ticket = options.Get(IdentityConstants.BearerScheme).RefreshTokenProtector.Unprotect(req.RefreshToken);
            if (ticket?.Properties.ExpiresUtc is not { } expires || time.GetUtcNow() >= expires ||
                await signIn.ValidateSecurityStampAsync(ticket.Principal) is not { } user)
                return Results.Unauthorized();
            var sid = ticket.Principal.FindFirstValue(SessionStore.Claim);
            if (!await sessions.IsActiveAsync(sid, user.UserName)) return Results.Unauthorized();
            var principal = await signIn.CreateUserPrincipalAsync(user);
            ((ClaimsIdentity)principal.Identity!).AddClaim(new(SessionStore.Claim, sid!));
            return Results.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
        });

        app.MapGet("/api/account/me", (ClaimsPrincipal u) =>
            new AccountInfo(u.Identity!.Name!, u.IsInRole(AccountEndpoints.AdminRole), u.FindFirstValue(SessionStore.Claim))).RequireAuthorization();

        var mine = app.MapGroup("/api/account/sessions").RequireAuthorization();
        mine.MapGet("", (ClaimsPrincipal u, SessionStore s) => s.ListAsync(u.Identity!.Name!, u.FindFirstValue(SessionStore.Claim)));
        mine.MapPost("/{id}/revoke", async (string id, ClaimsPrincipal u, SessionStore s) =>
            await s.RevokeAsync(id, u.Identity!.Name!) ? Results.NoContent() : Results.NotFound());
        mine.MapPost("/revoke-all", async (ClaimsPrincipal u, SessionStore s) => Results.Ok(await s.RevokeAllAsync(u.Identity!.Name!)));

        // Cambio password: serve quella attuale; le altre sessioni web/app si chiudono (chi conosceva la vecchia è fuori).
        app.MapPost("/api/account/password", async (ChangePasswordRequest req, ClaimsPrincipal u, UserManager<AppUser> users, SessionStore s) =>
        {
            var user = await users.FindByNameAsync(u.Identity!.Name!);
            if (user is null) return Results.Unauthorized();
            var r = await users.ChangePasswordAsync(user, req.CurrentPassword ?? "", req.NewPassword ?? "");
            if (!r.Succeeded) return Results.Text(string.Join(" ", r.Errors.Select(e => e.Description)), statusCode: 400);
            await s.RevokeAllAsync(user.UserName!, except: u.FindFirstValue(SessionStore.Claim), logins: true);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting(AuthRateLimit);

        // Token per agenti esterni (MCP). Non si crea un token con un altro token: serve un login vero (web, app, CLI).
        app.MapPost("/api/account/tokens", async (NewApiTokenRequest req, ClaimsPrincipal u, SessionStore s) =>
        {
            try { return Results.Ok(await s.CreateTokenAsync(u.Identity!.Name!, req.Name)); }
            catch (ArgumentException ex) { return Results.Text(ex.Message, statusCode: 400); }
        }).RequireAuthorization();
    }

    // I cookie si rinnovano col security stamp (CreateUserPrincipalAsync): senza questo il claim "sid" si perderebbe.
    public static void KeepSessionClaimOnCookieRefresh(SecurityStampValidatorOptions o) =>
        o.OnRefreshingPrincipal = ctx =>
        {
            if (ctx.CurrentPrincipal?.FindFirst(SessionStore.Claim) is { } sid && ctx.NewPrincipal?.Identity is ClaimsIdentity id)
                id.AddClaim(new(SessionStore.Claim, sid.Value));
            return Task.CompletedTask;
        };
}
