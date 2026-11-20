using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AgentWeb.Web;

// Il documento OpenAPI dichiara come ci si autentica: bearer (app, CLI, token MCP via /identity/login o /api/account/tokens)
// oppure il cookie del browser. Ogni operazione richiede uno dei due, tranne quelle [AllowAnonymous] (login, /healthz…).
public static class OpenApiSecurity
{
    public static void Configure(OpenApiOptions o)
    {
        o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title = "AgentWeb API";
            doc.Components ??= new();
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer",
                Description = "Access token da POST /identity/login (rinnovo con /identity/refresh) o token MCP.",
            };
            doc.Components.SecuritySchemes["Cookie"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = ".AspNetCore.Identity.Application",
                Description = "Cookie di login del browser (POST /account/login).",
            };
            return Task.CompletedTask;
        });
        o.AddOperationTransformer((op, ctx, _) =>
        {
            var metadata = ctx.Description.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<IAllowAnonymous>().Any() || !metadata.OfType<IAuthorizeData>().Any()) return Task.CompletedTask;
            op.Security ??= [];
            op.Security.Add(new() { [new OpenApiSecuritySchemeReference("Bearer", ctx.Document)] = [] });
            op.Security.Add(new() { [new OpenApiSecuritySchemeReference("Cookie", ctx.Document)] = [] });
            return Task.CompletedTask;
        });
    }
}
