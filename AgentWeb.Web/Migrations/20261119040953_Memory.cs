using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261119040953_Memory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
            .Annotation("Npgsql:PostgresExtension:vector", ",,");

        migrationBuilder.CreateTable(
            name: "Memories",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Scope = table.Column<string>(type: "text", nullable: false),
                AgentName = table.Column<string>(type: "text", nullable: true),
                ChannelId = table.Column<int>(type: "integer", nullable: true),
                Kind = table.Column<string>(type: "text", nullable: false),
                Title = table.Column<string>(type: "text", nullable: true),
                Content = table.Column<string>(type: "text", nullable: false),
                SourceId = table.Column<string>(type: "text", nullable: true),
                UpToMessageId = table.Column<long>(type: "bigint", nullable: true),
                CreatedBy = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Memories", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Memories_Scope_AgentName",
            table: "Memories",
            columns: new[] { "Scope", "AgentName" });

        migrationBuilder.CreateIndex(
            name: "IX_Memories_Scope_ChannelId",
            table: "Memories",
            columns: new[] { "Scope", "ChannelId" });

        // Embedding della memoria (pgvector): non mappato da EF, lo gestisce MemoryStore. 768 = dimensioni di embeddinggemma.
        // HNSW con distanza coseno: ricerca veloce anche con molti ricordi.
        migrationBuilder.Sql("""ALTER TABLE "Memories" ADD COLUMN "Embedding" vector(768);""");
        migrationBuilder.Sql("""CREATE INDEX "IX_Memories_Embedding" ON "Memories" USING hnsw ("Embedding" vector_cosine_ops);""");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Memories");

        migrationBuilder.AlterDatabase()
            .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
    }
}
