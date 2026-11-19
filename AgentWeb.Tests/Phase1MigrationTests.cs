using AgentWeb.Web;
using Microsoft.EntityFrameworkCore;

namespace AgentWeb.Tests;

// Fase 1 — persistenza su Postgres: migrazioni idempotenti e schema allineato al modello.
public sealed class Phase1MigrationTests : IDisposable
{
    readonly string database = TestDb.NewName();

    AppDb NewDb() => new(new DbContextOptionsBuilder<AppDb>().UseNpgsql(TestDb.ConnectionString(database)).Options);

    [Fact]
    public async Task MissingDatabase_IsCreated_WithEveryMigration()
    {
        await using var db = NewDb();
        await Database.MigrateAsync(db);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task SecondRun_IsANoOp_AndKeepsData()
    {
        await using (var db = NewDb())
        {
            await Database.MigrateAsync(db);
            db.Channels.Add(new ChannelEntity { Name = "resta", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb())
        {
            await Database.MigrateAsync(db);
            Assert.Equal(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("resta", (await db.Channels.SingleAsync()).Name);
        }
    }

    // Chi cambia il modello senza aggiungere la migrazione fa fallire questo test (e non il deploy).
    [Fact]
    public void Model_HasNoChangesWithoutAMigration()
    {
        using var db = NewDb();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    // Più repliche avviate insieme: tutte partono, lo schema viene applicato una volta sola.
    [Fact]
    public async Task ConcurrentStartups_MigrateOnce()
    {
        await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var db = NewDb();
            await Database.MigrateAsync(db);
        }));
        await using var check = NewDb();
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
    }

    public void Dispose() => TestDb.Drop(database);
}
