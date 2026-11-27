using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261126095208_MessagesChannelIdIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Messages_ChannelId",
            table: "Messages");

        migrationBuilder.CreateIndex(
            name: "IX_Messages_ChannelId_Id",
            table: "Messages",
            columns: new[] { "ChannelId", "Id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Messages_ChannelId_Id",
            table: "Messages");

        migrationBuilder.CreateIndex(
            name: "IX_Messages_ChannelId",
            table: "Messages",
            column: "ChannelId");
    }
}
