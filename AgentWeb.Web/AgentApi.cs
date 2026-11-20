using System.Security.Claims;
using AGUI.Abstractions;
using AGUI.Formatting;
using AGUI.Server;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using AgentWeb.Shared.Agents;
using AgentWeb.Shared.Workspace;
using Microsoft.Extensions.AI;

namespace AgentWeb.Web;

// API usata dall'app MAUI (iPhone/Android), autenticata con i token di /identity/login.
public static class AgentApi
{
    public const string ChatRateLimit = "chat";

    sealed record NameBody(string Name);
    sealed record StatusBody(WorkStatus Status);
    sealed record DocumentBody(string FileName, string Text);

    public static void MapAgentApi(this WebApplication app)
    {
        // Ogni rotta del workspace passa da WorkspaceStore con l'utente: la visibilità la decide solo ChannelVisibility.
        var ws = app.MapGroup("/api/workspace").RequireAuthorization().AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (KeyNotFoundException ex) { return Results.Text(ex.Message, statusCode: 404); }
            catch (UnauthorizedAccessException ex) { return Results.Text(ex.Message, statusCode: 403); }
            catch (ArgumentException ex) { return Results.Text(ex.Message, statusCode: 400); }
        });
        ws.MapGet("/channels", (WorkspaceStore s, ClaimsPrincipal u) => s.GetChannelsAsync(User(u)));
        ws.MapPost("/channels", (NewChannelRequest b, WorkspaceStore s, ClaimsPrincipal u) => s.CreateChannelAsync(b.Name, User(u), b.IsPrivate));
        ws.MapGet("/channels/{id:int}/members", (int id, WorkspaceStore s, ClaimsPrincipal u) => s.GetMembersAsync(id, User(u)));
        ws.MapPost("/channels/{id:int}/members", async (int id, NameBody b, WorkspaceStore s, ClaimsPrincipal u) =>
        {
            await s.AddMemberAsync(id, User(u), b.Name);
            return Results.NoContent();
        });
        ws.MapGet("/channels/{id:int}/memory", (int id, WorkspaceStore s, ClaimsPrincipal u) => s.GetChannelMemoryAsync(id, User(u)));
        ws.MapPost("/channels/{id:int}/documents", async (int id, DocumentBody b, WorkspaceStore s, ClaimsPrincipal u) =>
            Results.Ok(await s.UploadDocumentAsync(id, User(u), b.FileName, b.Text)));
        ws.MapDelete("/channels/{id:int}/memory/{memoryId:long}", async (int id, long memoryId, WorkspaceStore s, ClaimsPrincipal u) =>
        {
            await s.DeleteChannelMemoryAsync(id, User(u), memoryId, null);
            return Results.NoContent();
        });
        ws.MapDelete("/channels/{id:int}/documents/{sourceId}", async (int id, string sourceId, WorkspaceStore s, ClaimsPrincipal u) =>
        {
            await s.DeleteChannelMemoryAsync(id, User(u), 0, sourceId);
            return Results.NoContent();
        });
        ws.MapGet("/channels/{id:int}/agents", (int id, WorkspaceStore s, ClaimsPrincipal u) => s.GetChannelAgentsAsync(id, User(u)));
        ws.MapPost("/channels/{id:int}/agents", async (int id, NameBody b, WorkspaceStore s, ClaimsPrincipal u) =>
        {
            await s.AddChannelAgentAsync(id, User(u), b.Name);
            return Results.NoContent();
        });
        ws.MapDelete("/channels/{id:int}/agents/{agent}", async (int id, string agent, WorkspaceStore s, ClaimsPrincipal u) =>
        {
            await s.RemoveChannelAgentAsync(id, User(u), agent);
            return Results.NoContent();
        });
        ws.MapGet("/channels/{id:int}/messages", (int id, WorkspaceStore s, ClaimsPrincipal u) => s.GetMessagesAsync(id, User(u)));
        ws.MapGet("/channels/{id:int}/messages/since/{afterId:long}", (int id, long afterId, WorkspaceStore s, ClaimsPrincipal u) =>
            s.GetMessagesSinceAsync(id, User(u), afterId));
        ws.MapPost("/channels/{id:int}/messages", async (int id, NewMessageRequest b, WorkspaceStore s, ClaimsPrincipal u) =>
        {
            await s.PostUserMessageAsync(id, User(u), b.Text, b.ClientId, b.ParentId);
            return Results.NoContent();
        });
        ws.MapGet("/channels/{id:int}/messages/{rootId:long}/thread", (int id, long rootId, WorkspaceStore s, ClaimsPrincipal u) => s.GetThreadAsync(id, User(u), rootId));
        ws.MapPost("/channels/{id:int}/read", async (int id, MarkReadRequest b, WorkspaceStore s, ClaimsPrincipal u) =>
        {
            await s.MarkReadAsync(id, User(u), b.LastId);
            return Results.NoContent();
        });
        ws.MapGet("/search", (string q, WorkspaceStore s, ClaimsPrincipal u) => s.SearchAsync(User(u), q));
        ws.MapPost("/channels/{id:int}/threads/{rootId:long}/read", async (int id, long rootId, MarkReadRequest b, WorkspaceStore s, ClaimsPrincipal u) =>
        {
            await s.MarkThreadReadAsync(id, User(u), rootId, b.LastId);
            return Results.NoContent();
        });
        ws.MapGet("/feed", (WorkspaceStore s, ClaimsPrincipal u) => s.GetFeedAsync(User(u)));
        ws.MapGet("/activity", (WorkspaceStore s, AgentActivity a, ClaimsPrincipal u) => s.GetActivityAsync(User(u), a));
        ws.MapGet("/tasks", (WorkspaceStore s, ClaimsPrincipal u) => s.GetAllTasksAsync(User(u)));
        ws.MapPost("/direct", (NameBody b, WorkspaceStore s, ClaimsPrincipal u) => s.OpenDirectAsync(User(u), b.Name));
        ws.MapGet("/people", (WorkspaceStore s, ClaimsPrincipal u) => s.GetPeopleAsync(User(u)));
        ws.MapGet("/channels/{id:int}/tasks", (int id, WorkspaceStore s, ClaimsPrincipal u) => s.GetTasksAsync(id, User(u)));
        ws.MapPost("/channels/{id:int}/tasks", (int id, NewTaskRequest t, WorkspaceStore s, ClaimsPrincipal u) => s.CreateTaskAsync(id, t, User(u)));
        ws.MapPost("/tasks/{id:int}/status", async (int id, StatusBody b, WorkspaceStore s, ClaimsPrincipal u) =>
        {
            await s.SetStatusByUserAsync(id, b.Status, User(u));
            return Results.NoContent();
        });
        ws.MapGet("/agents", (WorkspaceStore s) => s.GetAgentsAsync());
        ws.MapGet("/agents/icons", (WorkspaceStore s) => s.GetAgentIconsAsync());

        var settings = app.MapGroup("/api/settings").RequireAuthorization(AccountEndpoints.AdminRole);
        settings.MapGet("", (AgentSettingsStore store) => store.GetAsync());
        settings.MapPut("", (AgentSettings body, AgentSettingsStore store) =>
        {
            var errors = body.Validate(store.AllowStdio);
            if (errors.Count > 0) return Results.Text(string.Join("\n", errors), statusCode: 400);
            store.Save(body);
            return Results.NoContent();
        });
        settings.MapPost("/test/mcp", (McpServerConfig s, AgentSettingsStore store, CancellationToken ct) => TestAsync(() => store.TestMcpAsync(s, ct)));
        settings.MapPost("/test/a2a", (A2AAgentConfig a, AgentSettingsStore store, CancellationToken ct) => TestAsync(() => store.TestA2AAsync(a, ct)));
        settings.MapPost("/llm/models", async (LlmProviderConfig p, AgentSettingsStore store, CancellationToken ct) =>
        {
            try { return Results.Ok(await store.ListModelsAsync(p, ct).WaitAsync(TimeSpan.FromSeconds(30), ct)); }
            catch (Exception ex) { return Results.Text(ex.GetBaseException().Message, statusCode: 502); }
        });
        settings.MapPost("/test/llm", (LlmProviderConfig p, AgentSettingsStore store, CancellationToken ct) => TestAsync(() => store.TestLlmAsync(p, ct), seconds: 120)); // un modello locale a freddo è lento

        // Protocollo AG-UI (RunAgentInput -> eventi SSE): qualsiasi client AG-UI (AGUIChatClient, CopilotKit, @ag-ui/client) può usare il dibattito.
        app.MapPost("/api/chat", async (RunAgentInput input, IChatClient chat, IAGUIEventStreamFormatter formatter,
            IOptions<JsonOptions> json, HttpContext http, CancellationToken ct) =>
        {
            var ctx = input.ToChatRequestContext(json.Value.SerializerOptions);
            var events = chat.GetStreamingResponseAsync(ctx.Messages, ctx.ChatOptions, ct).AsAGUIEventStreamAsync(ctx, ct);
            http.Response.ContentType = formatter.MediaType;
            http.Response.Headers.CacheControl = "no-cache";
            await formatter.WriteAsync(events, http.Response.Body, ct);
        }).RequireAuthorization().RequireRateLimiting(ChatRateLimit);
    }

    static string User(ClaimsPrincipal u) => u.Identity?.Name ?? throw new UnauthorizedAccessException();

    static async Task<IResult> TestAsync(Func<Task<string>> test, int seconds = 30)
    {
        try { return Results.Ok(await test().WaitAsync(TimeSpan.FromSeconds(seconds))); }
        catch (Exception ex) { return Results.Text(ex.GetBaseException().Message, statusCode: 502); }
    }
}
