using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.AuthService.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginEventsFr18 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "login_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_login_events_created_at",
                table: "login_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_login_events_user_id",
                table: "login_events",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_events");
        }
    }
}
