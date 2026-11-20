using System.Security.Claims;
using AgentWeb.Web;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWeb.Tests;

// Connessioni SignalR per sessione: la revoca chiude solo le proprie, e una sessione svuotata e riaperta resta revocabile.
public sealed class SessionConnectionsTests
{
    sealed class FakeContext(string id) : HubCallerContext
    {
        public bool Aborted { get; private set; }
        public override string ConnectionId => id;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => default;
        public override void Abort() => Aborted = true;
    }

    [Fact]
    public void ASessionEmptiedAndReopened_IsStillAborted()
    {
        var c = new SessionConnections();
        var first = new FakeContext("c1");
        c.Add("s", first);
        c.Remove("s", "c1");
        var second = new FakeContext("c2");
        c.Add("s", second);
        c.Abort("s");
        Assert.False(first.Aborted);
        Assert.True(second.Aborted);
    }

    [Fact]
    public async Task RevokingSomeoneElsesSession_DoesNotCloseItsConnections()
    {
        using var app = new TestApp();
        await app.CreateUserAsync("alice@test.it");
        await app.CreateUserAsync("bob@test.it");
        await app.LoginTokensAsync("alice@test.it");
        using var scope = app.Services.CreateScope();
        var sid = (await scope.ServiceProvider.GetRequiredService<SessionStore>().ListAsync("alice@test.it", null)).Single().Id;
        var ctx = new FakeContext("c1");
        app.Services.GetRequiredService<SessionConnections>().Add(sid, ctx);

        var store = scope.ServiceProvider.GetRequiredService<SessionStore>();
        Assert.False(await store.RevokeAsync(sid, "bob@test.it"));
        Assert.False(ctx.Aborted);
        Assert.True(await store.RevokeAsync(sid, "alice@test.it"));
        Assert.True(ctx.Aborted);
    }
}
