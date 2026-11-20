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
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry (tracce → Tempo, metriche → Prometheus, log → Loki, tutto su Grafana) via OTLP verso un collector:
// attivo solo se è configurato OTEL_EXPORTER_OTLP_ENDPOINT (es. http://localhost:4318 con docker-compose.observability.yml).
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "agentweb"))
        .WithTracing(t => t
            .AddSource(AgentWeb.Web.AgentActivity.Name, "Npgsql")
            .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/healthz"))
            .AddHttpClientInstrumentation())
        .WithMetrics(m => m
            .AddMeter(AgentWeb.Web.AgentActivity.Name, "Npgsql")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation())
        .WithLogging()
        .UseOtlpExporter();

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
builder.Services.AddOpenApi(OpenApiSecurity.Configure); // /openapi/v1.json (OpenAPI 3.2, default di .NET 11) per SDK e client generati
builder.Services.AddSingleton<DaemonBroker>();   // lavoro degli agenti "daemon" verso i computer degli utenti
builder.Services.AddHostedService(sp => sp.GetRequiredService<DaemonBroker>()); // scadenza dei lease
builder.Services.AddSingleton<AgentSettingsStore>();
builder.Services.AddSingleton<LlmClients>();
builder.Services.AddSingleton<AgentIcons>();
builder.Services.AddSingleton<MemoryStore>();   // memoria semantica (pgvector + embedding locale)
builder.Services.AddSingleton<SafeFetch>();     // fetch_url sicuro per gli agenti
builder.Services.AddSingleton<NativeTools>();
builder.Services.AddSingleton<AGUI.Formatting.IAGUIEventStreamFormatter, AGUI.Formatting.SseEventStreamFormatter>();
builder.Services.AddScoped<IChatClient>(sp => new DebateChatClient(sp.GetRequiredService<LlmClients>(), sp.GetRequiredService<AgentSettingsStore>()));

// Workspace: canali, messaggi, task, eventi in tempo reale e worker degli agenti.
builder.Services.AddSingleton<WorkspaceBus>();
builder.Services.AddSingleton<AgentQueue>();
builder.Services.AddSingleton<AgentActivity>();
builder.Services.AddSingleton<WorkspaceStore>();
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<WorkspaceHubBridge>();
builder.Services.AddScoped<IWorkspaceClient, LocalWorkspaceClient>();
builder.Services.AddScoped<IUserSession, LocalUserSession>();
builder.Services.AddScoped<IAccountClient, LocalAccountClient>();
builder.Services.AddScoped<IAgentSettingsService, LocalAgentSettingsService>();

// Compressione delle risposte: zstd (RFC 8878) per chi lo accetta, poi Brotli e Gzip (default di ASP.NET Core 11).
// Niente su HTTPS (EnableForHttps resta false): con cookie e token nelle pagine si apre BREACH.
// Dietro un proxy TLS (Caddy) IsHttps è vero solo con ASPNETCORE_FORWARDEDHEADERS_ENABLED=true (nel compose del VPS);
// e il proxy stesso non deve comprimere l'HTML (Caddy: encode solo per css/js/wasm/svg/font).
builder.Services.AddResponseCompression();

var app = builder.Build();
app.UseResponseCompression();

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
app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/openapi") && !ctx.Request.Path.StartsWithSegments("/healthz") && !ctx.Request.Path.StartsWithSegments("/identity") && !ctx.Request.Path.StartsWithSegments("/hubs") && !ctx.Request.Path.StartsWithSegments("/mcp") && !ctx.Request.Path.StartsWithSegments("/a2a"),
    b => b.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

// Ogni endpoint dichiara la sua policy (RoutePolicyTests fallisce altrimenti): file statici esplicitamente pubblici.
app.MapStaticAssets().AllowAnonymous();
app.MapAccountEndpoints();
app.MapTwoFactorEndpoints();
app.MapSessionEndpoints(); // /identity/login e /identity/refresh con sessione; MapIdentityApi non è più mappato (niente self-signup)
// Riconnessione con stato: in una breve perdita di rete i messaggi restano nel buffer e vengono reinviati,
// senza nuova connessione né replay del canale (oltre il buffer subentra la riconnessione normale + replay).
app.MapHub<WorkspaceHub>("/hubs/workspace", o => o.AllowStatefulReconnects = true);
app.MapMcp("/mcp").RequireAuthorization("mcp");
app.MapA2AExposure();
app.MapAgentApi();
app.MapDaemonEndpoints();
app.MapOpenApi().RequireAuthorization(); // l'elenco delle rotte non è pubblico
// Health check per load balancer e container: 200 se il database risponde, 503 altrimenti. Nessun dato.
app.MapGet("/healthz", async (AppDb db, CancellationToken ct) => await db.Database.CanConnectAsync(ct) ? Results.Ok("ok") : Results.StatusCode(503)).AllowAnonymous();

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
