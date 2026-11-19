using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;
using AgentWeb.Shared.Workspace;
using AgentWeb.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWeb.Tests;

// Il server vero (Program.cs) in memoria, con un database Postgres temporaneo tutto suo
// (o condiviso con un'altra istanza, per simulare due repliche).
public sealed class TestApp(Dictionary<string, string?>? settings = null, string? sharedDatabase = null) : WebApplicationFactory<Program>
{
    public const string Password = "Test123!x";

    public string Dir { get; } = Directory.CreateTempSubdirectory("agentweb-test-").FullName;
    public string Database { get; } = sharedDatabase ?? TestDb.NewName();

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment("Development");
        b.UseSetting("ConnectionStrings:Default", TestDb.ConnectionString(Database));
        b.UseSetting("AgentSettings:Path", Path.Combine(Dir, "agentsettings.json")); // mai le impostazioni reali dell'utente
        b.UseSetting("Auth:RateLimitPerMinute", "10000");
        foreach (var (k, v) in settings ?? []) b.UseSetting(k, v);
    }

    public async Task CreateUserAsync(string email, bool admin = false)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var r = await users.CreateAsync(new AppUser { UserName = email, Email = email }, Password);
        Assert.True(r.Succeeded, string.Join(" ", r.Errors.Select(e => e.Description)));
        if (admin)
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roles.RoleExistsAsync(AccountEndpoints.AdminRole)) await roles.CreateAsync(new IdentityRole(AccountEndpoints.AdminRole));
            await users.AddToRoleAsync((await users.FindByNameAsync(email))!, AccountEndpoints.AdminRole);
        }
    }

    public sealed record Tokens(string AccessToken, string RefreshToken);

    public async Task<Tokens> LoginTokensAsync(string email)
    {
        using var http = CreateClient();
        var r = await http.PostAsJsonAsync("/identity/login", new { email, password = Password });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<Tokens>())!;
    }

    public HttpClient Bearer(string accessToken)
    {
        var http = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return http;
    }

    public async Task<HttpClient> LoginAsync(string email) => Bearer((await LoginTokensAsync(email)).AccessToken);

    // SignalR sul TestServer: long polling (il TestServer non espone socket), token nell'header Authorization.
    public async Task<Live> ConnectAsync(string accessToken, bool messagePack = false)
    {
        var builder = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "hubs/workspace"), o =>
            {
                o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            });
        if (messagePack) builder.AddMessagePackProtocol(); // come l'app iOS/Android
        var hub = builder.Build();
        var live = new Live(hub);
        await hub.StartAsync();
        return live;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (sharedDatabase is null) TestDb.Drop(Database); // il DB condiviso lo elimina l'istanza che l'ha creato
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

// Connessione SignalR che raccoglie gli eventi ricevuti.
public sealed class Live : IAsyncDisposable
{
    readonly Channel<WorkspaceEvent> events = Channel.CreateUnbounded<WorkspaceEvent>();
    public HubConnection Hub { get; }
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Live(HubConnection hub)
    {
        Hub = hub;
        hub.On<WorkspaceEvent>("event", e => events.Writer.TryWrite(e));
        hub.Closed += _ => { Closed.TrySetResult(); return Task.CompletedTask; };
    }

    public Task<bool> WatchAsync(int? channelId) => Hub.InvokeAsync<bool>("Watch", channelId);

    // Tutti gli eventi che soddisfano il filtro arrivati entro il tempo dato.
    public async Task<List<WorkspaceEvent>> CollectAsync(Func<WorkspaceEvent, bool> filter, TimeSpan window, int stopAfter = int.MaxValue)
    {
        var got = new List<WorkspaceEvent>();
        using var cts = new CancellationTokenSource(window);
        try
        {
            while (got.Count < stopAfter && await events.Reader.WaitToReadAsync(cts.Token))
                while (got.Count < stopAfter && events.Reader.TryRead(out var e))
                    if (filter(e)) got.Add(e);
        }
        catch (OperationCanceledException) { }
        return got;
    }

    public ValueTask DisposeAsync() => Hub.DisposeAsync();
}
