using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceStateCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_session_criterion_evidence_state",
                table: "session_criterion_evidence",
                sql: "state IN ('UNKNOWN', 'PARTIAL', 'SATISFIED', 'FAILED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_session_criterion_evidence_state",
                table: "session_criterion_evidence");
        }
    }
}
