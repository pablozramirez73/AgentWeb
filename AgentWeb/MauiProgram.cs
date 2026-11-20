using Microsoft.Extensions.Logging;
using AgentWeb.Shared.Services;
using AgentWeb.Services;
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Workspace;
using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWeb;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // Add device-specific services used by the AgentWeb.Shared project
        builder.Services.AddSingleton<IFormFactor, FormFactor>();

        // L'app è un client: agenti, canali e dati vivono sul server AgentWeb.Web (/api, /identity, /hubs).
        var server = new Uri(ServerUrl);
        // Errore rapido se il server non risponde; nessun timeout totale perché il dibattito può durare minuti.
        // AutomaticDecompression: chiede e decomprime zstd (poi br/gzip) per le risposte dell'API.
        static SocketsHttpHandler Handler() => new() { ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = System.Net.DecompressionMethods.All };
        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton(sp => new RemoteUserSession(
            WithUserAgent(new HttpClient(Handler()) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) }), sp.GetRequiredService<ITokenStore>()));
        builder.Services.AddSingleton<IUserSession>(sp => sp.GetRequiredService<RemoteUserSession>());
        builder.Services.AddSingleton(sp => WithUserAgent(new HttpClient(new BearerHandler(sp.GetRequiredService<RemoteUserSession>()) { InnerHandler = Handler() })
            { BaseAddress = server, Timeout = Timeout.InfiniteTimeSpan }));
        builder.Services.AddSingleton<IWorkspaceClient>(sp => new RemoteWorkspaceClient(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<RemoteUserSession>().GetAccessTokenAsync,
            hub => hub.AddMessagePackProtocol())); // binario: meno byte per evento sulla rete mobile
        builder.Services.AddSingleton<IChatClient>(sp => new AGUI.Client.AGUIChatClient(new(sp.GetRequiredService<HttpClient>(), "api/chat")));
        builder.Services.AddSingleton<IAgentSettingsService, RemoteAgentSettingsService>();
        builder.Services.AddSingleton<IAccountClient>(sp => new RemoteAccountClient(sp.GetRequiredService<HttpClient>()));

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    // Il server salva lo User-Agent con la sessione: nella pagina "Dispositivi" si riconosce quale telefono revocare.
    static HttpClient WithUserAgent(HttpClient http)
    {
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            $"AgentWeb-App/{AppInfo.Current.VersionString} ({DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString}; {DeviceInfo.Current.Model})");
        return http;
    }

    // Indirizzo del server, scelto in fase di build: dotnet build -p:AgentWebServer=http://macbook.local:5294/
    // Default http://localhost:5294/: simulatore iOS (localhost è il Mac) ed emulatore Android con "adb reverse tcp:5294 tcp:5294".
    // iPhone/telefono vero: il nome .local del Mac (HTTP consentito solo in rete locale). shortcut: in produzione HTTPS.
    static readonly string ServerUrl = typeof(MauiProgram).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).Cast<System.Reflection.AssemblyMetadataAttribute>()
        .First(a => a.Key == "AgentWebServer").Value!;
}

// Il refresh token resta nel portachiavi del sistema (Keychain / Android Keystore), non in chiaro.
sealed class SecureTokenStore : ITokenStore
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string? value)
    {
        if (value is null) { SecureStorage.Default.Remove(key); return Task.CompletedTask; }
        return SecureStorage.Default.SetAsync(key, value);
    }
}
