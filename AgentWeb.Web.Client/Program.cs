using AgentWeb.Shared.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.AI;

// Le pagine con @rendermode InteractiveWebAssembly (dibattito, canali) girano qui, nel browser.
// Gli agenti (Ollama, MCP, A2A) e i dati restano sul server: il browser usa /api, /hubs/workspace e AG-UI su /api/chat,
// stessa origine, quindi il cookie di login viaggia da solo (niente token in localStorage).
var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Nessun timeout totale: il dibattito può durare minuti.
builder.Services.AddScoped(sp => new HttpClient(new SessionExpiredHandler(sp.GetRequiredService<NavigationManager>()))
    { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress), Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddScoped<IChatClient>(sp => new AGUI.Client.AGUIChatClient(new(sp.GetRequiredService<HttpClient>(), "api/chat")));
builder.Services.AddScoped<IWorkspaceClient>(sp => new RemoteWorkspaceClient(sp.GetRequiredService<HttpClient>()));
builder.Services.AddScoped<IAccountClient>(sp => new RemoteAccountClient(sp.GetRequiredService<HttpClient>()));

await builder.Build().RunAsync();

// Cookie scaduto: le /api rispondono 401 (non un redirect), quindi si ricarica la pagina e il server porta al login.
sealed class SessionExpiredHandler(NavigationManager nav) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) nav.Refresh(forceReload: true);
        return response;
    }
}
