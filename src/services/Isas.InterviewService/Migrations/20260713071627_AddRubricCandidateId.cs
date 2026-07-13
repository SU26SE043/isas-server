using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRubricCandidateId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "candidate_id",
                table: "rubric_criteria",
                type: "uuid",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000001"),
                column: "candidate_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000002"),
                column: "candidate_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000003"),
                column: "candidate_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0b100000-0000-0000-0000-000000000004"),
                column: "candidate_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000001"),
                column: "candidate_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000002"),
                column: "candidate_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000003"),
                column: "candidate_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0be00000-0000-0000-0000-000000000004"),
                column: "candidate_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000001"),
                column: "candidate_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000002"),
                column: "candidate_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000003"),
                column: "candidate_id",
                value: null);

            migrationBuilder.UpdateData(
                table: "rubric_criteria",
                keyColumn: "id",
                keyValue: new Guid("0fe00000-0000-0000-0000-000000000004"),
                column: "candidate_id",
                value: null);

            migrationBuilder.CreateIndex(
                name: "ix_rubric_criteria_candidate_id_job_category_is_active",
                table: "rubric_criteria",
                columns: new[] { "candidate_id", "job_category", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_rubric_criteria_candidate_id_job_category_is_active",
                table: "rubric_criteria");

            migrationBuilder.DropColumn(
                name: "candidate_id",
                table: "rubric_criteria");
        }
    }
}
