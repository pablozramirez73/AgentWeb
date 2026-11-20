using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261120214203_ThreadReadCursors : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ThreadReadCursors",
            columns: table => new
            {
                UserName = table.Column<string>(type: "text", nullable: false),
                RootId = table.Column<long>(type: "bigint", nullable: false),
                LastReadId = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ThreadReadCursors", x => new { x.UserName, x.RootId });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ThreadReadCursors");
    }
}
