using A2A;
using A2A.AspNetCore;
using AgentWeb.Shared.Agents;

namespace AgentWeb.Web;

// Agenti con protocollo "A2A": ognuno è un agente A2A su /a2a/<nome> (JSON-RPC), con la sua Agent Card in
// /a2a/<nome>/.well-known/agent-card.json. La card è pubblica (scoperta); i messaggi richiedono un token API (awt_...).
// Un solo server A2A per tutti: l'agente da eseguire si legge dal percorso della richiesta (le impostazioni cambiano a caldo).
public sealed class ExposedAgentHandler(AgentRuntime runtime, AgentSettingsStore store, IHttpContextAccessor http) : IAgentHandler
{
    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var responder = new MessageResponder(eventQueue, context.ContextId);
        var name = http.HttpContext?.GetRouteValue("agent") as string;
        if (name is null || A2AExposure.Find(store.Get(), name) is not { } agent)
        {
            await responder.ReplyAsync("Agente inesistente o non esposto via A2A.", cancellationToken: cancellationToken);
            return;
        }
        var answer = await runtime.AskAsync(agent.Name, context.UserText ?? "", cancellationToken);
        await responder.ReplyAsync(answer.Length > 0 ? answer : "(nessuna risposta)", cancellationToken: cancellationToken);
    }
}

public static class A2AExposure
{
    public static LocalAgentConfig? Find(AgentSettings s, string name) =>
        s.FindLocal(name) is { Protocol: AgentProtocols.A2A } a ? a : null;

    public static void AddA2AExposure(this IServiceCollection services)
    {
        services.AddSingleton<ExposedAgentHandler>();
        services.AddSingleton<ITaskStore, InMemoryTaskStore>();
        services.AddSingleton<ChannelEventNotifier>();
        services.AddSingleton(sp => new A2AServer(sp.GetRequiredService<ExposedAgentHandler>(), sp.GetRequiredService<ITaskStore>(),
            sp.GetRequiredService<ChannelEventNotifier>(), sp.GetRequiredService<ILogger<A2AServer>>()));
    }

    public static void MapA2AExposure(this WebApplication app)
    {
        app.MapA2A(app.Services.GetRequiredService<A2AServer>(), "/a2a/{agent}").RequireAuthorization("mcp");

        app.MapGet("/a2a/{agent}/.well-known/agent-card.json", (string agent, AgentSettingsStore store, HttpContext http) =>
        {
            if (Find(store.Get(), agent) is not { } a) return Results.NotFound();
            var url = $"{http.Request.Scheme}://{http.Request.Host}/a2a/{Uri.EscapeDataString(a.Name)}";
            var card = new AgentCard
            {
                Name = a.Name,
                Description = a.Prompt,
                Version = "1.0.0",
                SupportedInterfaces = [new AgentInterface { Url = url, ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
                DefaultInputModes = ["text/plain"],
                DefaultOutputModes = ["text/plain"],
                Capabilities = new AgentCapabilities { Streaming = false },
                Skills = [new AgentSkill { Id = "ask", Name = a.Name, Description = a.Prompt, Tags = ["agentweb"] }],
            };
            return Results.Text(System.Text.Json.JsonSerializer.Serialize(card, A2AJsonUtilities.DefaultOptions), "application/json");
        }).AllowAnonymous();
    }
}
