using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AgentWeb.Web;

// Schema gestito solo da migrazioni EF (dotnet ef migrations add <Nome> -o Migrations), su Postgres.
public static class Database
{
    // Chiave fissa dell'advisory lock di Postgres che serializza le migrazioni tra repliche/processi.
    const long MigrationLock = 0x4167656E74576562; // "AgentWeb"

    // Idempotente: DB inesistente -> lo crea e applica tutto; DB aggiornato -> nessuna operazione.
    // Più repliche avviate insieme: una sola applica le migrazioni, le altre aspettano il lock e trovano lo schema già pronto
    // (il lock interno di EF non basta con il provider Npgsql attuale: il test ConcurrentStartups lo dimostrava).
    public static async Task MigrateAsync(AppDb db, CancellationToken ct = default)
    {
        // Database momentaneamente irraggiungibile all'avvio (es. DNS di OrbStack/Docker che riparte): riprova prima di arrendersi.
        for (var attempt = 1; ; attempt++)
        {
            try { await MigrateOnceAsync(db, ct); return; }
            catch (NpgsqlException ex) when (ex.IsTransient && attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    static async Task MigrateOnceAsync(AppDb db, CancellationToken ct)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct))
        {
            try { await creator.CreateAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DuplicateDatabase or PostgresErrorCodes.UniqueViolation) { } // creato da un'altra replica
        }

        await db.Database.OpenConnectionAsync(ct); // stessa connessione per lock, migrazioni e unlock
        try
        {
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({MigrationLock})", ct);
            try { await db.Database.MigrateAsync(ct); }
            finally { await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({MigrationLock})", CancellationToken.None); }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
