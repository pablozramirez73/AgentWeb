using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261120211701_PushNotifications : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "NotifyMentions",
            table: "AspNetUsers",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "NotifyPrivate",
            table: "AspNetUsers",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "NotifyTasks",
            table: "AspNetUsers",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.CreateTable(
            name: "PushDevices",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserName = table.Column<string>(type: "text", nullable: false),
                Sid = table.Column<string>(type: "text", nullable: true),
                Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                Token = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                P256dh = table.Column<string>(type: "text", nullable: true),
                Auth = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PushDevices", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PushDevices_Sid",
            table: "PushDevices",
            column: "Sid");

        migrationBuilder.CreateIndex(
            name: "IX_PushDevices_Token",
            table: "PushDevices",
            column: "Token",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PushDevices_UserName",
            table: "PushDevices",
            column: "UserName");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PushDevices");

        migrationBuilder.DropColumn(
            name: "NotifyMentions",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "NotifyPrivate",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "NotifyTasks",
            table: "AspNetUsers");
    }
}
