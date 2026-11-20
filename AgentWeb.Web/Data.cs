using AgentWeb.Shared.Workspace;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AgentWeb.Web;

public sealed class AppUser : IdentityUser;

public sealed class ChannelEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsPrivate { get; set; } // privato: lo vedono solo i membri (ChannelVisibility), nemmeno gli admin
    public string? CreatedBy { get; set; } // chi l'ha creato: è l'unico che sceglie gli agenti del canale
}

// Agenti del canale: solo loro rispondono alle @menzioni, ricevono task e si delegano lavoro in questo canale.
public sealed class ChannelAgentEntity
{
    public int ChannelId { get; set; }
    public string AgentName { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
}

// Memoria degli agenti. Scope "agent": privata dell'agente (lo segue in ogni canale); "channel": condivisa dagli agenti del canale.
// Kind: "fact" (ricordo), "document" (pezzo di un documento caricato, SourceId = documento), "summary" (riassunto fino a UpToMessageId).
// L'embedding (vector(768), pgvector) non è mappato da EF: lo scrive e lo legge MemoryStore con SQL.
public sealed class MemoryEntity
{
    public long Id { get; set; }
    public string Scope { get; set; } = "";
    public string? AgentName { get; set; }
    public int? ChannelId { get; set; }
    public string Kind { get; set; } = "";
    public string? Title { get; set; }
    public string Content { get; set; } = "";
    public string? SourceId { get; set; }
    public long? UpToMessageId { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

// Membri dei canali privati (nei pubblici entra chiunque abbia un account).
public sealed class ChannelMemberEntity
{
    public int ChannelId { get; set; }
    public string UserName { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class MessageEntity
{
    public long Id { get; set; }
    public int ChannelId { get; set; }
    public string Author { get; set; } = "";
    public AuthorKind Kind { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? ClientId { get; set; } // chiave di idempotenza scelta dal client: un retry non duplica il messaggio
    public long? ParentId { get; set; }   // radice del thread (sempre un messaggio senza ParentId dello stesso canale)
}

// Fin dove un utente ha letto un canale: i non letti sono i messaggi altrui con Id maggiore. Avanza soltanto.
public sealed class ReadCursorEntity
{
    public string UserName { get; set; } = "";
    public int ChannelId { get; set; }
    public long LastReadId { get; set; }
}

// Lavoro per un daemon (agente "daemon"): in tabella perché più repliche condividano coda e lease.
public sealed class DaemonDeliveryEntity
{
    public string Id { get; set; } = "";
    public string Owner { get; set; } = "";   // minuscolo
    public string Runtime { get; set; } = ""; // minuscolo
    public string Prompt { get; set; } = "";
    public string? Sid { get; set; }          // sessione del daemon che l'ha presa (null = in coda)
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public bool Done { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// Un daemon collegato, per sessione: vivo finché fa poll o heartbeat.
public sealed class DaemonMachineEntity
{
    public string Sid { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Runtimes { get; set; } = [];
    public DateTimeOffset LastSeen { get; set; }
}

// Una riga per login (cookie web o token app): la revoca ha effetto alla richiesta successiva.
public sealed class UserSessionEntity
{
    public string Id { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Kind { get; set; } = ""; // "web" | "app" | "token" (token API per agenti esterni via MCP)
    public string? SecretHash { get; set; } // solo "token": SHA-256 del segreto, il segreto in chiaro non viene salvato
    public string? Device { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class TaskEntity
{
    public int Id { get; set; }
    public int ChannelId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Assignee { get; set; }
    public bool AssigneeIsAgent { get; set; }
    public WorkStatus Status { get; set; }
    public string? Result { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AppDb(DbContextOptions<AppDb> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<ChannelEntity> Channels => Set<ChannelEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<ChannelMemberEntity> ChannelMembers => Set<ChannelMemberEntity>();
    public DbSet<UserSessionEntity> UserSessions => Set<UserSessionEntity>();
    public DbSet<ChannelAgentEntity> ChannelAgents => Set<ChannelAgentEntity>();
    public DbSet<MemoryEntity> Memories => Set<MemoryEntity>();
    public DbSet<ReadCursorEntity> ReadCursors => Set<ReadCursorEntity>();
    public DbSet<DaemonDeliveryEntity> DaemonDeliveries => Set<DaemonDeliveryEntity>();
    public DbSet<DaemonMachineEntity> DaemonMachines => Set<DaemonMachineEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<ChannelEntity>().HasIndex(c => c.Name).IsUnique();
        b.Entity<MessageEntity>().HasIndex(m => m.ChannelId);
        b.Entity<MessageEntity>().HasIndex(m => new { m.ChannelId, m.Author, m.ClientId }).IsUnique().HasFilter("\"ClientId\" IS NOT NULL");
        b.Entity<MessageEntity>().HasIndex(m => m.ParentId).HasFilter("\"ParentId\" IS NOT NULL");
        // Ricerca full-text dei messaggi (to_tsvector 'simple': nessuna lingua imposta, parole intere).
        b.Entity<MessageEntity>().HasIndex(m => m.Text).HasMethod("GIN").IsTsVectorExpressionIndex("simple");
        b.Entity<ReadCursorEntity>().HasKey(r => new { r.UserName, r.ChannelId });
        b.Entity<DaemonDeliveryEntity>().HasIndex(d => new { d.Owner, d.Sid });
        b.Entity<DaemonMachineEntity>().HasKey(m => m.Sid);
        b.Entity<DaemonMachineEntity>().HasIndex(m => m.Owner);
        b.Entity<TaskEntity>().HasIndex(t => t.ChannelId);
        b.Entity<ChannelMemberEntity>().HasKey(m => new { m.ChannelId, m.UserName });
        b.Entity<ChannelAgentEntity>().HasKey(a => new { a.ChannelId, a.AgentName });
        b.Entity<ChannelMemberEntity>().HasIndex(m => m.UserName);
        b.Entity<UserSessionEntity>().HasIndex(s => s.UserName);
        b.Entity<MemoryEntity>().HasIndex(m => new { m.Scope, m.ChannelId });
        b.Entity<MemoryEntity>().HasIndex(m => new { m.Scope, m.AgentName });
        b.HasPostgresExtension("vector");
    }
}
