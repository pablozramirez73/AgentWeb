using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AgentWeb.Tests;

// Un database Postgres usa-e-getta per test, sullo stesso server dell'app (agentweb ha CREATEDB).
// Server: AGENTWEB_TEST_PG, altrimenti la stringa di connessione negli user-secrets del progetto web.
public static class TestDb
{
    static readonly Lazy<string> Base = new(() =>
        Environment.GetEnvironmentVariable("AGENTWEB_TEST_PG")
        ?? new ConfigurationBuilder().AddUserSecrets<Program>().Build().GetConnectionString("Default")
        ?? throw new InvalidOperationException("Serve Postgres: AGENTWEB_TEST_PG o user-secrets ConnectionStrings:Default del progetto web."));

    public static string NewName() => "agentweb_test_" + Guid.NewGuid().ToString("N")[..12];

    public static string ConnectionString(string database) => new NpgsqlConnectionStringBuilder(Base.Value) { Database = database }.ConnectionString;

    // Il ruolo dell'app non è superutente: se una connessione di un'altra sessione è ancora aperta (pool, servizi in chiusura)
    // il DROP ... WITH (FORCE) può essere rifiutato per un attimo. Si riprova invece di dare privilegi in più al ruolo.
    public static void Drop(string database)
    {
        for (var attempt = 1; ; attempt++)
        {
            NpgsqlConnection.ClearAllPools();
            try
            {
                using var conn = new NpgsqlConnection(ConnectionString("postgres"));
                conn.Open();
                using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", conn);
                cmd.ExecuteNonQuery();
                return;
            }
            catch (PostgresException ex) when (ex.SqlState is "42501" or "55006" && attempt < 20) { Thread.Sleep(250); }
        }
    }
}
