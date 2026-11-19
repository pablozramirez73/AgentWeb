using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentWeb.Shared.Agents;

// Client dell'API del server (AgentWeb.Web /api): usati dall'app MAUI, dove non girano né Ollama né MCP/A2A.
// L'HttpClient arriva già con BaseAddress e BearerHandler (token di login).
public sealed class RemoteAgentSettingsService(HttpClient http) : IAgentSettingsService
{
    public async Task<AgentSettingsView> GetAsync(CancellationToken ct = default)
    {
        using var r = await http.GetAsync("api/settings", ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<AgentSettingsView>(ct))!;
    }

    public async Task SaveAsync(AgentSettings settings, CancellationToken ct = default)
    {
        using var r = await http.PutAsJsonAsync("api/settings", settings, ct);
        await EnsureOkAsync(r, ct);
    }

    public Task<string> TestMcpAsync(McpServerConfig server, CancellationToken ct = default) => PostTextAsync("api/settings/test/mcp", server, ct);

    public Task<string> TestA2AAsync(A2AAgentConfig agent, CancellationToken ct = default) => PostTextAsync("api/settings/test/a2a", agent, ct);

    public Task<string> TestLlmAsync(LlmProviderConfig provider, CancellationToken ct = default) => PostTextAsync("api/settings/test/llm", provider, ct);

    public async Task<List<string>> ListModelsAsync(LlmProviderConfig provider, CancellationToken ct = default)
    {
        using var r = await http.PostAsJsonAsync("api/settings/llm/models", provider, ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<List<string>>(ct))!;
    }

    async Task<string> PostTextAsync<T>(string url, T body, CancellationToken ct)
    {
        using var r = await http.PostAsJsonAsync(url, body, ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<string>(ct))!;
    }

    // Il server risponde agli errori con text/plain: lo mostriamo così com'è nella UI.
    public static async Task EnsureOkAsync(HttpResponseMessage r, CancellationToken ct)
    {
        if (r.IsSuccessStatusCode) return;
        var text = await r.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(text.Length > 0 ? text : $"Server: HTTP {(int)r.StatusCode}");
    }
}
