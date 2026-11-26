using System.Globalization;
using System.Text;
using AgentWeb.Shared.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Npgsql;
using OllamaSharp;

namespace AgentWeb.Web;

public sealed record MemoryHit(long Id, string Scope, string Kind, string? Title, string Content, double Distance);

// Memoria semantica degli agenti su pgvector. Embedding con un modello locale (default: embeddinggemma su Ollama, 768 dimensioni).
// Ricerca: distanza coseno (<=>) con indice HNSW. Se il servizio di embedding non risponde la memoria degrada (nessun risultato),
// non blocca gli agenti.
public sealed class MemoryStore(IDbContextFactory<AppDb> dbf, IConfiguration config, ILogger<MemoryStore> log)
{
    public const int Dimensions = 768;
    const int EmbedBatch = 64;
    const int ChunkChars = 900, ChunkOverlap = 150, MaxDocumentChars = 1_000_000;

    IEmbeddingGenerator<string, Embedding<float>>? embedder;
    IEmbeddingGenerator<string, Embedding<float>> Embedder => embedder ??= new OllamaApiClient(
        new HttpClient { BaseAddress = new Uri((config["Memory:EmbeddingUrl"] ?? config["Ollama:Url"] ?? "http://localhost:11434").TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) },
        config["Memory:EmbeddingModel"] ?? "embeddinggemma");

    // Per i test: un generatore finto al posto di Ollama.
    public void UseEmbedder(IEmbeddingGenerator<string, Embedding<float>> generator) => embedder = generator;

    // ---------- scrittura ----------

    public Task<long> RememberAsync(string scope, string? agent, int? channelId, string content, string createdBy, CancellationToken ct = default) =>
        AddAsync(new MemoryEntity { Scope = scope, AgentName = agent, ChannelId = channelId, Kind = "fact", Content = Clean(content, 2000), CreatedBy = createdBy }, ct);

    public async Task<int> AddDocumentAsync(int channelId, string fileName, string text, string user, CancellationToken ct = default)
    {
        if (text.Length > MaxDocumentChars) throw new ArgumentException("Documento troppo grande: massimo circa 1 MB di testo.");
        var chunks = Chunk(text).ToList();
        if (chunks.Count == 0) throw new ArgumentException("Il documento è vuoto.");
        // A gruppi: un documento da 1 MB sono ~1.300 pezzi, troppi per una sola chiamata entro il timeout dell'embedding.
        var vectors = new List<float[]>(chunks.Count);
        foreach (var batch in chunks.Chunk(EmbedBatch)) vectors.AddRange(await EmbedAsync(batch, ct));
        var source = Guid.NewGuid().ToString("N");
        await using var db = await dbf.CreateDbContextAsync(ct);
        var rows = chunks.Select(c => new MemoryEntity
        {
            Scope = "channel", ChannelId = channelId, Kind = "document", Title = Clean(fileName, 200), Content = c, SourceId = source,
            CreatedBy = user, CreatedAt = DateTimeOffset.UtcNow,
        }).ToList();
        db.Memories.AddRange(rows);
        await db.SaveChangesAsync(ct);
        for (var i = 0; i < rows.Count; i += EmbedBatch) // un UPDATE per gruppo invece di uno per pezzo
        {
            var n = Math.Min(EmbedBatch, rows.Count - i);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "Memories" m SET "Embedding" = CAST(v.e AS vector)
                FROM unnest(@ids, @vecs) AS v(id, e) WHERE m."Id" = v.id
                """, [new NpgsqlParameter("ids", rows.Skip(i).Take(n).Select(r => r.Id).ToArray()),
                      new NpgsqlParameter("vecs", vectors.Skip(i).Take(n).Select(Literal).ToArray())], ct);
        }
        return rows.Count;
    }

    async Task<long> AddAsync(MemoryEntity m, CancellationToken ct)
    {
        var vector = (await EmbedAsync([m.Content], ct))[0];
        await using var db = await dbf.CreateDbContextAsync(ct);
        m.CreatedAt = DateTimeOffset.UtcNow;
        db.Memories.Add(m);
        await db.SaveChangesAsync(ct);
        await SetEmbeddingAsync(db, m.Id, vector, ct);
        return m.Id;
    }

    static Task SetEmbeddingAsync(AppDb db, long id, float[] vector, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("UPDATE \"Memories\" SET \"Embedding\" = CAST({0} AS vector) WHERE \"Id\" = {1}", [Literal(vector), id], ct);

    // ---------- ricerca ----------

    // Ricordi dell'agente + memoria del canale (fatti, documenti, riassunti), dal più vicino alla domanda.
    public async Task<List<MemoryHit>> SearchAsync(string agent, int? channelId, string query, int k = 5, CancellationToken ct = default)
    {
        float[] q;
        try { q = (await EmbedAsync([query], ct))[0]; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Memoria non disponibile (embedding)");
            return [];
        }
        await using var db = await dbf.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT "Id", "Scope", "Kind", "Title", "Content", "Embedding" <=> CAST(@q AS vector) AS distance
            FROM "Memories"
            WHERE "Embedding" IS NOT NULL
              AND (("Scope" = 'agent' AND "AgentName" = @agent) OR ("Scope" = 'channel' AND "ChannelId" = @channel))
            ORDER BY distance
            LIMIT @k
            """, conn);
        cmd.Parameters.AddWithValue("q", Literal(q));
        cmd.Parameters.AddWithValue("agent", agent);
        cmd.Parameters.AddWithValue("channel", (object?)channelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("k", Math.Clamp(k, 1, 20));
        var hits = new List<MemoryHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            hits.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetDouble(5)));
        return hits;
    }

    // ---------- gestione ----------

    // Un agente dimentica solo ciò che può vedere: i suoi ricordi o quelli del canale in cui lavora.
    public async Task<bool> ForgetAsync(long id, string agent, int? channelId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Memories.Where(m => m.Id == id && m.Kind != "document"
                && ((m.Scope == "agent" && m.AgentName == agent) || (m.Scope == "channel" && m.ChannelId == channelId)))
            .ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<List<MemoryDto>> ListChannelAsync(int channelId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var rows = await db.Memories.AsNoTracking().Where(m => m.Scope == "channel" && m.ChannelId == channelId).OrderByDescending(m => m.Id).ToListAsync(ct);
        var docs = rows.Where(m => m.Kind == "document").GroupBy(m => m.SourceId)
            .Select(g => new MemoryDto(g.Min(m => m.Id), "document", g.First().Title, Preview(g.MinBy(m => m.Id)!.Content),
                g.First().CreatedBy, g.First().CreatedAt, g.Key, g.Count()));
        var others = rows.Where(m => m.Kind != "document").Select(m => new MemoryDto(m.Id, m.Kind, m.Title, m.Content, m.CreatedBy, m.CreatedAt, null, 1));
        return [.. others.Concat(docs).OrderByDescending(m => m.CreatedAt)];
    }

    // Anteprima del primo pezzo del documento (le righe sono ordinate per Id decrescente: First() è l'ultimo pezzo).
    static string Preview(string content) => content.Length > 200 ? content[..200] : content;

    public async Task<bool> DeleteChannelMemoryAsync(int channelId, long id, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Memories.Where(m => m.Id == id && m.Scope == "channel" && m.ChannelId == channelId && m.Kind != "document").ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<bool> DeleteDocumentAsync(int channelId, string sourceId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Memories.Where(m => m.SourceId == sourceId && m.Scope == "channel" && m.ChannelId == channelId).ExecuteDeleteAsync(ct) > 0;
    }

    // ---------- riassunti dei canali lunghi ----------

    public async Task<(string Text, long UpTo)?> LatestSummaryAsync(int channelId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var s = await db.Memories.AsNoTracking().Where(m => m.Scope == "channel" && m.ChannelId == channelId && m.Kind == "summary")
            .OrderByDescending(m => m.UpToMessageId).FirstOrDefaultAsync(ct);
        return s is null ? null : (s.Content, s.UpToMessageId ?? 0);
    }

    // Quando dopo l'ultimo riassunto si accumulano più di `threshold` messaggi, i più vecchi (tutti tranne gli ultimi `keep`)
    // vengono riassunti insieme al riassunto precedente. Il contesto degli agenti resta piccolo anche in canali lunghissimi.
    public async Task MaybeSummarizeAsync(int channelId, IChatClient llm, int threshold = 60, int keep = 30, CancellationToken ct = default)
    {
        var previous = await LatestSummaryAsync(channelId, ct);
        var upTo = previous?.UpTo ?? 0;
        await using var db = await dbf.CreateDbContextAsync(ct);
        var pending = await db.Messages.AsNoTracking().Where(m => m.ChannelId == channelId && m.Id > upTo).OrderBy(m => m.Id).ToListAsync(ct);
        if (pending.Count <= threshold) return;
        var old = pending.Take(pending.Count - keep).ToList();
        var transcript = string.Join("\n", old.Select(m => $"[{m.Author}]: {m.Text}"));
        var prompt = $"""
            Riassumi la conversazione di un canale di lavoro in italiano, in elenco puntato, massimo 250 parole.
            Conserva decisioni, numeri, nomi, compiti assegnati e questioni aperte. Niente frasi introduttive.
            {(previous is { } p ? $"Riassunto precedente da integrare:\n{p.Text}\n" : "")}
            Nuovi messaggi:
            {transcript}
            """;
        var summary = (await llm.GetResponseAsync([new(ChatRole.User, prompt)], cancellationToken: ct)).Text.Trim();
        if (summary.Length == 0) return;
        await AddAsync(new MemoryEntity
        {
            Scope = "channel", ChannelId = channelId, Kind = "summary", Title = $"Riassunto fino al messaggio {old[^1].Id}",
            Content = summary, UpToMessageId = old[^1].Id, CreatedBy = "Sistema",
        }, ct);
    }

    // ---------- utilità ----------

    async Task<float[][]> EmbedAsync(IList<string> texts, CancellationToken ct)
    {
        var result = await Embedder.GenerateAsync(texts, cancellationToken: ct);
        var vectors = result.Select(e => e.Vector.ToArray()).ToArray();
        if (vectors.Any(v => v.Length != Dimensions))
            throw new InvalidOperationException($"Il modello di embedding produce vettori da {vectors[0].Length} dimensioni, la memoria ne usa {Dimensions}.");
        return vectors;
    }

    static string Literal(float[] v) => "[" + string.Join(",", v.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";

    static string Clean(string s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }

    // Pezzi da ~900 caratteri sui confini di paragrafo, con 150 caratteri di sovrapposizione per non spezzare il contesto.
    public static IEnumerable<string> Chunk(string text)
    {
        text = text.Replace("\r\n", "\n").Trim();
        var sb = new StringBuilder();
        foreach (var para in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            for (var start = 0; start < para.Length; start += ChunkChars - ChunkOverlap)
            {
                var piece = para.Substring(start, Math.Min(ChunkChars, para.Length - start));
                if (sb.Length + piece.Length + 2 > ChunkChars && sb.Length > 0)
                {
                    yield return sb.ToString();
                    var tail = sb.ToString();
                    sb.Clear().Append(tail[Math.Max(0, tail.Length - ChunkOverlap)..]).Append("\n\n");
                }
                sb.Append(piece).Append("\n\n");
                if (start + ChunkChars >= para.Length) break;
            }
        }
        if (sb.ToString().Trim().Length > 0) yield return sb.ToString().Trim();
    }
}
