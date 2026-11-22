using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261120231937_TaskFromMessage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Agent",
            table: "Tasks",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "MessageId",
            table: "Tasks",
            type: "bigint",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Agent",
            table: "Tasks");

        migrationBuilder.DropColumn(
            name: "MessageId",
            table: "Tasks");
    }
}
