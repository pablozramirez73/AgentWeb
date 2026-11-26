using System.Net.Http.Json;
using AgentWeb.Shared.Workspace;
using AgentWeb.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWeb.Tests;

// Statistiche: ogni lavoro degli agenti finisce nello storico con i token del modello; ognuno vede solo i propri canali.
public sealed class StatsTests
{
    [Fact]
    public async Task Runs_AreStoredWithTokens_AndOnlyVisibleChannelsCount()
    {
        using var app = new TestApp();
        await app.CreateUserAsync("alice@test.it");
        await app.CreateUserAsync("bob@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        using var bob = await app.LoginAsync("bob@test.it");
        var secret = await CreateChannelAsync(alice, "segreto", true);
        var open = await CreateChannelAsync(alice, "generale", false);

        var activity = app.Services.GetRequiredService<AgentActivity>();
        await activity.TrackAsync(new AgentJob(open, "Ricercatore", null, 0), () =>
        {
            AgentActivity.Current.Value!.Add(new UsageDetails { InputTokenCount = 1200, OutputTokenCount = 300 }, "gemma4");
            AgentActivity.Current.Value!.Add(new UsageDetails { InputTokenCount = 800, OutputTokenCount = 100 }, "gemma4");
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            activity.TrackAsync(new AgentJob(secret, "Ricercatore", 7, 0), () => throw new InvalidOperationException("modello irraggiungibile")));

        var mine = (await alice.GetFromJsonAsync<StatsDto>("/api/workspace/stats?days=7"))!;
        Assert.Equal(7, mine.Daily.Count);
        Assert.Equal(2, mine.Daily[^1].Runs);
        Assert.Equal(1, mine.Daily[^1].Errors);
        Assert.Equal(2000, mine.Daily[^1].InputTokens);
        var agent = Assert.Single(mine.Agents);
        Assert.Equal((2, 1, 2000L, 400L), (agent.Runs, agent.Errors, agent.InputTokens, agent.OutputTokens));
        Assert.Equal("gemma4", Assert.Single(mine.Models).Model);
        Assert.Equal("modello irraggiungibile", Assert.Single(mine.Errors).Error);

        // Bob non vede il canale privato di Alice: né il lavoro fallito né il suo errore.
        var his = (await bob.GetFromJsonAsync<StatsDto>("/api/workspace/stats?days=7"))!;
        Assert.Equal((1, 0), (his.Daily[^1].Runs, his.Daily[^1].Errors));
        Assert.Empty(his.Errors);
    }

    // Redeploy: i lavori annullati dal loro token non sono errori e non entrano nelle statistiche;
    // un'annullamento che non viene dal token (es. timeout HTTP verso l'LLM) resta un errore.
    [Fact]
    public async Task CancelledRuns_AreNotErrors_ButTimeoutsAre()
    {
        using var app = new TestApp();
        await app.CreateUserAsync("alice@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        var channel = await CreateChannelAsync(alice, "generale", false);
        var activity = app.Services.GetRequiredService<AgentActivity>();

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            activity.TrackAsync(new AgentJob(channel, "Ricercatore", null, 0), () => Task.Delay(1000, stopping.Token), ct: stopping.Token));
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            activity.TrackAsync(new AgentJob(channel, "Ricercatore", null, 0), () => throw new TaskCanceledException("timeout del modello"), ct: CancellationToken.None));

        var stats = (await alice.GetFromJsonAsync<StatsDto>("/api/workspace/stats?days=1"))!;
        Assert.Equal((1, 1), (stats.Daily[^1].Runs, stats.Daily[^1].Errors));
        Assert.Equal("timeout del modello", Assert.Single(stats.Errors).Error);
        Assert.Single(activity.Snapshot().Recent);
    }

    // Statistiche → Agenti: lavori e messaggi di un agente (suoi, @menzioni, deleghe ricevute), solo nei canali visibili.
    [Fact]
    public async Task AgentLog_HasRunsAndConversation_OnlyInVisibleChannels()
    {
        using var app = new TestApp();
        await app.CreateUserAsync("alice@test.it");
        await app.CreateUserAsync("bob@test.it");
        using var alice = await app.LoginAsync("alice@test.it");
        using var bob = await app.LoginAsync("bob@test.it");
        var secret = await CreateChannelAsync(alice, "segreto", true);
        var open = await CreateChannelAsync(alice, "generale", false);

        var activity = app.Services.GetRequiredService<AgentActivity>();
        await activity.TrackAsync(new AgentJob(open, "Ricercatore", null, 1), () => Task.CompletedTask, "delega");
        await activity.TrackAsync(new AgentJob(secret, "Ricercatore", null, 0), () => Task.CompletedTask);
        await activity.TrackAsync(new AgentJob(open, "Scrittore", null, 0), () => Task.CompletedTask);
        var ws = app.Services.GetRequiredService<WorkspaceStore>();
        await ws.PostAsync(open, "Scrittore", AuthorKind.Agent, "→ chiedo a Ricercatore: fonti sul tema?", WorkspaceStore.NoMentions);
        await ws.PostAsync(open, "Ricercatore", AuthorKind.Agent, "Ecco tre fonti.", WorkspaceStore.NoMentions);
        await ws.PostAsync(open, "Scrittore", AuthorKind.Agent, "Bozza pronta.", WorkspaceStore.NoMentions);
        await ws.PostAsync(secret, "Ricercatore", AuthorKind.Agent, "Riservato.", WorkspaceStore.NoMentions);
        await ws.PostAsync(open, "Scrittore", AuthorKind.Agent, "Domanda per @RicercatoreSenior", WorkspaceStore.NoMentions);
        await ws.PostAsync(open, "Scrittore", AuthorKind.Agent, "Grazie @ricercatore!", WorkspaceStore.NoMentions);

        var mine = (await alice.GetFromJsonAsync<AgentLogDto>("/api/workspace/agents/Ricercatore/log?days=1"))!;
        Assert.Equal(2, mine.Runs.Count);
        Assert.Equal((2, 1), (mine.Totals.Sum(t => t.Runs), mine.Totals.Sum(t => t.Delegations)));
        Assert.Equal(["Grazie @ricercatore!", "Riservato.", "Ecco tre fonti.", "→ chiedo a Ricercatore: fonti sul tema?"], mine.Messages.Select(m => m.Message.Text));

        var his = (await bob.GetFromJsonAsync<AgentLogDto>("/api/workspace/agents/Ricercatore/log?days=1"))!;
        Assert.Equal("delega", Assert.Single(his.Runs).Kind);
        Assert.DoesNotContain(his.Messages, m => m.Message.ChannelId == secret);
        Assert.Equal(3, his.Messages.Count);
    }

    static async Task<int> CreateChannelAsync(HttpClient http, string name, bool isPrivate)
    {
        var r = await http.PostAsJsonAsync("/api/workspace/channels", new NewChannelRequest(name, isPrivate));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<ChannelDto>())!.Id;
    }
}
