using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261119203346_DaemonQueue : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DaemonDeliveries",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                Owner = table.Column<string>(type: "text", nullable: false),
                Runtime = table.Column<string>(type: "text", nullable: false),
                Prompt = table.Column<string>(type: "text", nullable: false),
                Sid = table.Column<string>(type: "text", nullable: true),
                LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Attempts = table.Column<int>(type: "integer", nullable: false),
                Done = table.Column<bool>(type: "boolean", nullable: false),
                Result = table.Column<string>(type: "text", nullable: true),
                Error = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DaemonDeliveries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "DaemonMachines",
            columns: table => new
            {
                Sid = table.Column<string>(type: "text", nullable: false),
                Owner = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Runtimes = table.Column<List<string>>(type: "text[]", nullable: false),
                LastSeen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DaemonMachines", x => x.Sid);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DaemonDeliveries_Owner_Sid",
            table: "DaemonDeliveries",
            columns: new[] { "Owner", "Sid" });

        migrationBuilder.CreateIndex(
            name: "IX_DaemonMachines_Owner",
            table: "DaemonMachines",
            column: "Owner");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DaemonDeliveries");

        migrationBuilder.DropTable(
            name: "DaemonMachines");
    }
}
