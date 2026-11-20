using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentWeb.Web.Migrations;

/// <inheritdoc />
public partial class _20261120205159_UserProfile : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "Avatar",
            table: "AspNetUsers",
            type: "bytea",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AvatarType",
            table: "AspNetUsers",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DisplayName",
            table: "AspNetUsers",
            type: "character varying(60)",
            maxLength: 60,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Avatar",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "AvatarType",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "DisplayName",
            table: "AspNetUsers");
    }
}
