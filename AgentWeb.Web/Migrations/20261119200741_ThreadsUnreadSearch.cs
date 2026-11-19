using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261119200741_ThreadsUnreadSearch : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ParentId",
            table: "Messages",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "ReadCursors",
            columns: table => new
            {
                UserName = table.Column<string>(type: "text", nullable: false),
                ChannelId = table.Column<int>(type: "integer", nullable: false),
                LastReadId = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReadCursors", x => new { x.UserName, x.ChannelId });
            });

        migrationBuilder.CreateIndex(
            name: "IX_Messages_ParentId",
            table: "Messages",
            column: "ParentId",
            filter: "\"ParentId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Messages_Text",
            table: "Messages",
            column: "Text")
            .Annotation("Npgsql:IndexMethod", "GIN")
            .Annotation("Npgsql:TsVectorConfig", "simple");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ReadCursors");

        migrationBuilder.DropIndex(
            name: "IX_Messages_ParentId",
            table: "Messages");

        migrationBuilder.DropIndex(
            name: "IX_Messages_Text",
            table: "Messages");

        migrationBuilder.DropColumn(
            name: "ParentId",
            table: "Messages");
    }
}
