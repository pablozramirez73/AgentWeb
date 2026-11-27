using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261126090020_ChannelNotify : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ChannelNotify",
            columns: table => new
            {
                UserName = table.Column<string>(type: "text", nullable: false),
                ChannelId = table.Column<int>(type: "integer", nullable: false),
                Level = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ChannelNotify", x => new { x.UserName, x.ChannelId });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ChannelNotify");
    }
}
