using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.AuthService.Migrations
{
    /// <inheritdoc />
    public partial class AddUserBanF20 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ban_reason",
                table: "users",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "banned_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "banned_by",
                table: "users",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ban_reason",
                table: "users");

            migrationBuilder.DropColumn(
                name: "banned_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "banned_by",
                table: "users");
        }
    }
}
