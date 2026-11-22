using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261122213501_UniqueTaskPerMessage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_Tasks_MessageId",
            table: "Tasks",
            column: "MessageId",
            unique: true,
            filter: "\"MessageId\" IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Tasks_MessageId",
            table: "Tasks");
    }
}
