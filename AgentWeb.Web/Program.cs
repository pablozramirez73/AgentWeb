using Microsoft.AspNetCore.Components; // AddAutoPause (pacchetto Microsoft.AspNetCore.Components.Server.AutoPause)
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Services;
using AgentWeb.Shared.Workspace;
using AgentWeb.Web;
using AgentWeb.Web.Components;
using AgentWeb.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents(); // pagine nel browser (AgentWeb.Web.Client), es. il dibattito
// SignalR: JSON (browser WebAssembly) e MessagePack binario, più compatto, per i client .NET nativi (app iOS/Android).
var signalR = builder.Services.AddSignalR().AddMessagePackProtocol();
// Più repliche dietro un load balancer: con "Redis" configurato gli eventi SignalR arrivano ai client di ogni replica.
if (builder.Configuration["Redis"] is { Length: > 0 } redis) signalR.AddStackExchangeRedis(redis, o => o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("agentweb"));

// Device-specific services used by the AgentWeb.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Utenti: cookie per il web, token bearer (/identity/login) per l'app MAUI.
// Postgres. In sviluppo la stringa di connessione sta in user-secrets (dotnet user-secrets set "ConnectionStrings:Default" ...),
// in produzione nella variabile d'ambiente ConnectionStrings__Default: mai nei file del repository.
builder.Services.AddDbContextFactory<AppDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Manca ConnectionStrings:Default (Postgres).")));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDb>>().CreateDbContext());
builder.Services.AddIdentityApiEndpoints<AppUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDb>();
builder.Services.ConfigureApplicationCookie(o => o.LoginPath = "/login");
builder.Services.AddAuthorizationBuilder().AddPolicy(AccountEndpoints.AdminRole, p => p.RequireRole(AccountEndpoints.AdminRole));
builder.Services.AddCascadingAuthenticationState();

// Sessioni revocabili: ogni login ha una riga in UserSessions e il claim "sid", verificato a ogni autenticazione.
builder.Services.AddSingleton<SessionConnections>();
builder.Services.AddSingleton<SessionStore>();

// Server MCP (/mcp) per agenti esterni (Claude, Hermes…): autenticazione con token API personali (Bearer awt_...).
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.Scheme, null);
builder.Services.AddAuthorizationBuilder().AddPolicy("mcp", p => p
    .AddAuthenticationSchemes(ApiTokenAuthenticationHandler.Scheme, IdentityConstants.BearerScheme)
    .RequireAuthenticatedUser());
builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "agentweb", Version = "0.6.0" })
    .WithHttpTransport(o => o.Stateless = true) // ogni richiesta porta il suo token: niente sessioni MCP da tenere in memoria
    .WithTools<AgentWebMcpTools>();
builder.Services.AddA2AExposure(); // agenti con protocollo A2A su /a2a/<nome>
builder.Services.ConfigureApplicationCookie(SessionValidation.ForCookie);
builder.Services.Configure<Microsoft.AspNetCore.Authentication.BearerToken.BearerTokenOptions>(IdentityConstants.BearerScheme, SessionValidation.ForBearer);
builder.Services.Configure<SecurityStampValidatorOptions>(SessionEndpoints.KeepSessionClaimOnCookieRefresh);
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, SessionRevalidatingAuthStateProvider>();

// Rate limit su login, refresh e registrazione, per IP (oltre al lockout per account di Identity).
var authPerMinute = builder.Configuration.GetValue("Auth:RateLimitPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(SessionEndpoints.AuthRateLimit, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// Agenti: ogni agente usa il provider LLM scelto nelle Impostazioni (Ollama locale/remoto/Cloud, OpenAI-compatibile, Anthropic).
builder.Services.AddSingleton<DaemonBroker>();   // lavoro degli agenti "daemon" verso i computer degli utenti
builder.Services.AddSingleton<AgentSettingsStore>();
builder.Services.AddSingleton<LlmClients>();
builder.Services.AddSingleton<MemoryStore>();   // memoria semantica (pgvector + embedding locale)
builder.Services.AddSingleton<SafeFetch>();     // fetch_url sicuro per gli agenti
builder.Services.AddSingleton<NativeTools>();
builder.Services.AddSingleton<AGUI.Formatting.IAGUIEventStreamFormatter, AGUI.Formatting.SseEventStreamFormatter>();
builder.Services.AddScoped<IChatClient>(sp => new DebateChatClient(sp.GetRequiredService<LlmClients>(), sp.GetRequiredService<AgentSettingsStore>()));

// Workspace: canali, messaggi, task, eventi in tempo reale e worker degli agenti.
builder.Services.AddSingleton<WorkspaceBus>();
builder.Services.AddSingleton<AgentQueue>();
builder.Services.AddSingleton<WorkspaceStore>();
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<WorkspaceHubBridge>();
builder.Services.AddScoped<IWorkspaceClient, LocalWorkspaceClient>();
builder.Services.AddScoped<IUserSession, LocalUserSession>();
builder.Services.AddScoped<IAccountClient, LocalAccountClient>();
builder.Services.AddScoped<IAgentSettingsService, LocalAgentSettingsService>();

var app = builder.Build();

// Schema dalle migrazioni EF (Migrations/); i DB creati con il vecchio EnsureCreated vengono agganciati al baseline.
using (var scope = app.Services.CreateScope())
    await Database.MigrateAsync(scope.ServiceProvider.GetRequiredService<AppDb>());

// Manutenzione: "-- reset-password <email> [file]" reimposta la password ed esce senza avviare il server.
if (args is ["reset-password", var resetEmail, ..])
    return await AccountMaintenance.ResetPasswordAsync(app.Services, resetEmail, args.ElementAtOrDefault(2));

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
// Le pagine d'errore HTML solo per il web: l'app deve ricevere 401/404 veri, non un redirect al login.
app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/identity") && !ctx.Request.Path.StartsWithSegments("/hubs") && !ctx.Request.Path.StartsWithSegments("/mcp") && !ctx.Request.Path.StartsWithSegments("/a2a"),
    b => b.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

// Ogni endpoint dichiara la sua policy (RoutePolicyTests fallisce altrimenti): file statici esplicitamente pubblici.
app.MapStaticAssets().AllowAnonymous();
app.MapAccountEndpoints();
app.MapSessionEndpoints(); // /identity/login e /identity/refresh con sessione; MapIdentityApi non è più mappato (niente self-signup)
// Riconnessione con stato: in una breve perdita di rete i messaggi restano nel buffer e vengono reinviati,
// senza nuova connessione né replay del canale (oltre il buffer subentra la riconnessione normale + replay).
app.MapHub<WorkspaceHub>("/hubs/workspace", o => o.AllowStatefulReconnects = true);
app.MapMcp("/mcp").RequireAuthorization("mcp");
app.MapA2AExposure();
app.MapAgentApi();
app.MapDaemonEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    // Pagine Blazor Server (es. Agenti): con la scheda nascosta da 60 s il circuito va in pausa e libera connessione e memoria;
    // tornando sulla scheda riprende dallo stato salvato nel browser.
    .WithBrowserOptions(o => o.AddAutoPause(p => p.HiddenDelay = TimeSpan.FromSeconds(60)))
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(AgentWeb.Shared._Imports).Assembly, typeof(AgentWeb.Web.Client._Imports).Assembly)
    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = IdentityConstants.ApplicationScheme }); // senza login -> /login

app.Run();
return 0;

public partial class Program; // per WebApplicationFactory nei test
