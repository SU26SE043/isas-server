using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionCriterionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_criterion_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    criterion_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    evidence_found = table.Column<string>(type: "jsonb", nullable: false),
                    missing_evidence = table.Column<string>(type: "jsonb", nullable: false),
                    deep_count = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_criterion_evidence", x => x.id);
                    table.ForeignKey(
                        name: "fk_session_criterion_evidence_practice_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "practice_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_session_criterion_evidence_rubric_criteria_criterion_id",
                        column: x => x.criterion_id,
                        principalTable: "rubric_criteria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_session_criterion_evidence_criterion_id",
                table: "session_criterion_evidence",
                column: "criterion_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_criterion_evidence_session_id_criterion_id",
                table: "session_criterion_evidence",
                columns: new[] { "session_id", "criterion_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_criterion_evidence");
        }
    }
}
