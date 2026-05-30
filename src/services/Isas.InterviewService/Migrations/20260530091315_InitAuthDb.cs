using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class InitAuthDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    original_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    storage_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    storage_bucket = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    parsed_text = table.Column<string>(type: "text", nullable: true),
                    parse_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_files", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_files_parse_status",
                table: "files",
                column: "parse_status");

            migrationBuilder.CreateIndex(
                name: "idx_files_user",
                table: "files",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "idx_files_user_type",
                table: "files",
                columns: new[] { "user_id", "file_type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "files");
        }
    }
}
