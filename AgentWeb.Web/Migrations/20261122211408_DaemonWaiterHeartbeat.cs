using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261122211408_DaemonWaiterHeartbeat : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "WaiterSeenAt",
            table: "DaemonDeliveries",
            type: "timestamp with time zone",
            nullable: false,
            defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "WaiterSeenAt",
            table: "DaemonDeliveries");
    }
}
