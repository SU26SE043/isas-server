using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionResultBC9 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "answered_count",
                table: "practice_sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "overall_score",
                table: "practice_sessions",
                type: "numeric(5,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "session_criterion_scores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    average_score = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    max_score = table.Column<int>(type: "integer", nullable: false),
                    percentage = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    weight = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    needs_improvement = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_criterion_scores", x => x.id);
                    table.ForeignKey(
                        name: "fk_session_criterion_scores_practice_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "practice_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_session_criterion_scores_rubric_criteria_criterion_id",
                        column: x => x.criterion_id,
                        principalTable: "rubric_criteria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_session_criterion_scores_criterion_id",
                table: "session_criterion_scores",
                column: "criterion_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_criterion_scores_session_id_criterion_id",
                table: "session_criterion_scores",
                columns: new[] { "session_id", "criterion_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_criterion_scores");

            migrationBuilder.DropColumn(
                name: "answered_count",
                table: "practice_sessions");

            migrationBuilder.DropColumn(
                name: "overall_score",
                table: "practice_sessions");
        }
    }
}
