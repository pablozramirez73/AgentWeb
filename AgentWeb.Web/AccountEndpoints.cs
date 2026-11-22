using Microsoft.AspNetCore.Authentication;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentWeb.Web;

// Login web (cookie) e gestione account. Niente self-signup: la registrazione libera vale solo per il primo account
// (che diventa amministratore); poi gli account li crea un admin con POST /api/admin/users.
public static class AccountEndpoints
{
    public const string AdminRole = "Admin";

    public sealed record Credentials(string Email, string Password);

    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapGet("/login", async (HttpContext http, IAntiforgery antiforgery, UserManager<AppUser> users, IAuthenticationSchemeProvider schemes, string? error) =>
        {
            var token = antiforgery.GetAndStoreTokens(http);
            var providers = (await schemes.GetAllSchemesAsync()).Select(s => s.Name).Where(ExternalProviders.Names.Contains).ToList();
            return Results.Content(LoginPage(token.FormFieldName, token.RequestToken!, error, bootstrap: !await users.Users.AnyAsync(), providers), "text/html; charset=utf-8");
        }).AllowAnonymous();

        // L'app chiede se mostrare "Registrati": vero solo finché non esiste nessun account.
        app.MapGet("/account/registration-open", async (UserManager<AppUser> users) => !await users.Users.AnyAsync()).AllowAnonymous();

        var account = app.MapGroup("/account").AllowAnonymous().RequireRateLimiting(SessionEndpoints.AuthRateLimit);

        account.MapPost("/login", async ([FromForm] string email, [FromForm] string password, [FromForm] string? code,
            UserManager<AppUser> users, SignInManager<AppUser> signIn, SessionStore sessions, HttpContext http) =>
        {
            var user = await users.FindByNameAsync(email?.Trim() ?? "");
            var r = user is null ? Microsoft.AspNetCore.Identity.SignInResult.Failed : await signIn.CheckPasswordSignInAsync(user, password ?? "", lockoutOnFailure: true);
            if (!r.Succeeded) return Back(r.IsLockedOut ? "Account bloccato per troppi tentativi: riprova più tardi." : "Email o password errate.");
            if (!await TwoFactor.PassesAsync(users, user!, code))
                return Back(string.IsNullOrWhiteSpace(code) ? "Inserisci il codice dell'app di autenticazione." : "Codice di verifica non valido.");
            await SignInWebAsync(user!, signIn, sessions, http);
            return Results.LocalRedirect("~/");
        });

        account.MapPost("/register-form", async ([FromForm] string email, [FromForm] string password,
            UserManager<AppUser> users, RoleManager<IdentityRole> roles, SignInManager<AppUser> signIn, SessionStore sessions, HttpContext http) =>
        {
            if (await users.Users.AnyAsync()) return Back(ClosedMessage);
            var (user, error) = await CreateUserAsync(users, roles, email, password, admin: true);
            if (user is null) return Back(error!);
            await SignInWebAsync(user, signIn, sessions, http);
            return Results.LocalRedirect("~/");
        });

        // JSON (app MAUI): stessa regola, aperta solo per creare il primo amministratore.
        account.MapPost("/register", async (Credentials c, UserManager<AppUser> users, RoleManager<IdentityRole> roles) =>
        {
            if (await users.Users.AnyAsync()) return Results.Text(ClosedMessage, statusCode: 403);
            var (_, error) = await CreateUserAsync(users, roles, c.Email, c.Password, admin: true);
            return error is null ? Results.NoContent() : Results.Text(error, statusCode: 400);
        });

        // shortcut: logout via GET (link semplice dal layout); il rischio CSRF è solo un logout forzato.
        app.MapGet("/logout", async (ClaimsPrincipal u, SignInManager<AppUser> signIn, SessionStore sessions) =>
        {
            if (u.FindFirstValue(SessionStore.Claim) is { } sid) await sessions.RevokeAsync(sid, u.Identity?.Name);
            await signIn.SignOutAsync();
            return Results.Redirect("/login");
        }).AllowAnonymous();

        var admin = app.MapGroup("/api/admin").RequireAuthorization(AdminRole);
        admin.MapGet("/users", (UserManager<AppUser> users) => users.Users.OrderBy(u => u.UserName).Select(u => u.UserName!).ToListAsync());
        admin.MapPost("/users", async (Credentials c, UserManager<AppUser> users, RoleManager<IdentityRole> roles) =>
        {
            var (user, error) = await CreateUserAsync(users, roles, c.Email, c.Password, admin: false);
            return user is null ? Results.Text(error!, statusCode: 400) : Results.Created($"/api/admin/users/{user.UserName}", user.UserName);
        });
        // Revoca tutte le sessioni di un utente (es. dispositivo perso): fuori subito da HTTP e realtime.
        admin.MapPost("/users/{userName}/revoke-sessions", async (string userName, SessionStore sessions) => Results.Ok(await sessions.RevokeAllAsync(userName)));
    }

    const string ClosedMessage = "Registrazione chiusa: chiedi un account a un amministratore.";

    internal static async Task SignInWebAsync(AppUser user, SignInManager<AppUser> signIn, SessionStore sessions, HttpContext http)
    {
        var sid = await sessions.CreateAsync(user.UserName!, "web", http);
        await signIn.SignInWithClaimsAsync(user, isPersistent: true, [new Claim(SessionStore.Claim, sid)]);
    }

    static async Task<(AppUser? User, string? Error)> CreateUserAsync(UserManager<AppUser> users, RoleManager<IdentityRole> roles, string email, string password, bool admin)
    {
        email = email?.Trim() ?? "";
        if (!new EmailAddressAttribute().IsValid(email)) return (null, "Email non valida.");
        var user = new AppUser { UserName = email, Email = email };
        var r = await users.CreateAsync(user, password ?? "");
        if (!r.Succeeded) return (null, string.Join(" ", r.Errors.Select(e => e.Description)));
        if (admin)
        {
            if (!await roles.RoleExistsAsync(AdminRole)) await roles.CreateAsync(new IdentityRole(AdminRole));
            await users.AddToRoleAsync(user, AdminRole);
        }
        return (user, null);
    }

    static IResult Back(string error) => Results.Redirect("/login?error=" + Uri.EscapeDataString(error));

    static string LoginPage(string field, string token, string? error, bool bootstrap, List<string> providers)
    {
        var e = HtmlEncoder.Default;
        // Solo per account già collegati (da Impostazioni → Account): i provider esterni non creano account.
        var external = providers.Count == 0 ? "" : $"""
            <div class="login-or text-muted small">oppure</div>
            {string.Concat(providers.Select(p => $"<a class=\"btn btn-outline-secondary w-100 mb-2\" href=\"/account/external/{e.Encode(p)}\">Continua con {e.Encode(p)}</a>"))}
            """;
        var register = bootstrap
            ? """<button class="btn btn-outline-secondary" type="submit" formaction="/account/register-form">Crea il primo account</button>"""
            : "";
        var hint = bootstrap
            ? "Nessun account esiste ancora: il primo che crei è amministratore. Password: almeno 6 caratteri con maiuscola, minuscola, numero e simbolo."
            : "Non hai un account? Chiedilo a un amministratore.";
        return $$"""
            <!DOCTYPE html>
            <html lang="it"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
            <meta name="color-scheme" content="light dark" />
            <title>Accedi · AgentWeb</title>
            <link rel="stylesheet" href="/_content/AgentWeb.Shared/lib/bootstrap/dist/css/bootstrap.min.css" />
            <link rel="stylesheet" href="/_content/AgentWeb.Shared/app.css" />
            <link rel="manifest" href="/_content/AgentWeb.Shared/manifest.webmanifest" />
            <link rel="apple-touch-icon" href="/_content/AgentWeb.Shared/icon-180.png" />
            <meta name="apple-mobile-web-app-title" content="AgentWeb" />
            <style>
              .login { min-height: 100dvh; display: grid; place-items: center; padding: 1.5rem; }
              .login-card { width: 100%; max-width: 400px; background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius-lg); padding: 2rem 1.75rem; box-shadow: var(--shadow); }
              .login-mark { width: 2rem; height: 2rem; margin-bottom: 1.25rem; fill: none; stroke: var(--text); stroke-width: 1.6; stroke-linecap: round; }
              .login-mark circle { fill: var(--text); stroke: none; }
              .login-card .btn-primary { flex: 1; }
              .login-or { display: flex; align-items: center; gap: .75rem; margin: 1.25rem 0 .75rem; }
              .login-or::before, .login-or::after { content: ""; flex: 1; border-top: 1px solid var(--border); }
            </style></head>
            <body><main class="login"><div class="login-card fade-in">
              <svg class="login-mark" viewBox="0 0 24 24" aria-hidden="true"><circle cx="6" cy="12" r="3" /><circle cx="18" cy="6" r="3" /><circle cx="18" cy="18" r="3" /><path d="M8.6 10.6 15.4 7.4M8.6 13.4l6.8 3.2" /></svg>
              <h1 class="h4 mb-1">Accedi ad AgentWeb</h1>
              <p class="text-muted small mb-4">Persone e agenti, nello stesso spazio.</p>
              {{(error is null ? "" : $"<div class=\"alert alert-danger py-2\" role=\"alert\">{e.Encode(error)}</div>")}}
              <form method="post" action="/account/login" class="d-flex flex-column gap-2">
                <input type="hidden" name="{{e.Encode(field)}}" value="{{e.Encode(token)}}" />
                <label class="form-label mb-0" for="email">Email</label>
                <input class="form-control" id="email" name="email" type="email" autocomplete="username" required autofocus />
                <label class="form-label mb-0" for="password">Password</label>
                <input class="form-control" id="password" name="password" type="password" autocomplete="current-password" required />
                <label class="form-label mb-0" for="code">Codice di verifica <span class="text-muted">(se attivo)</span></label>
                <input class="form-control" id="code" name="code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" />
                <div class="d-flex gap-2 mt-3">
                  <button class="btn btn-primary" type="submit">Accedi</button>
                  {{register}}
                </div>
                <p class="text-muted small mt-2 mb-0">{{hint}}</p>
              </form>
              {{external}}
            </div></main></body></html>
            """;
    }
}
