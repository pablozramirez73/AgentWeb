using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261119034709_ChannelAgents : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CreatedBy",
            table: "Channels",
            type: "text",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "ChannelAgents",
            columns: table => new
            {
                ChannelId = table.Column<int>(type: "integer", nullable: false),
                AgentName = table.Column<string>(type: "text", nullable: false),
                AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ChannelAgents", x => new { x.ChannelId, x.AgentName });
            });

        // Canali già esistenti: il creatore si ricava dal primo messaggio di sistema "<utente> ha creato il canale…" (Kind 2 = System).
        migrationBuilder.Sql("""
            UPDATE "Channels" AS c SET "CreatedBy" = first.author
            FROM (SELECT DISTINCT ON ("ChannelId") "ChannelId", split_part("Text", ' ha creato il canale', 1) AS author
                  FROM "Messages" WHERE "Kind" = 2 AND "Text" LIKE '% ha creato il canale%' ORDER BY "ChannelId", "Id") AS first
            WHERE first."ChannelId" = c."Id" AND c."CreatedBy" IS NULL;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ChannelAgents");

        migrationBuilder.DropColumn(
            name: "CreatedBy",
            table: "Channels");
    }
}
