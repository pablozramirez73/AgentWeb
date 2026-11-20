using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261120224534_DirectMessages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DirectKey",
            table: "Channels",
            type: "text",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Channels_DirectKey",
            table: "Channels",
            column: "DirectKey",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Channels_DirectKey",
            table: "Channels");

        migrationBuilder.DropColumn(
            name: "DirectKey",
            table: "Channels");
    }
}
