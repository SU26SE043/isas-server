using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptTemplatesF21 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "prompt_version",
                table: "answer_scores",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "prompt_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    change_note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_templates", x => x.id);
                    table.CheckConstraint("ck_prompt_templates_version_positive", "version > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_prompt_templates_key_version",
                table: "prompt_templates",
                columns: new[] { "key", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_prompt_templates_active_key",
                table: "prompt_templates",
                column: "key",
                unique: true,
                filter: "is_active");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prompt_templates");

            migrationBuilder.DropColumn(
                name: "prompt_version",
                table: "answer_scores");
        }
    }
}
