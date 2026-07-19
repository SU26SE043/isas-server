using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryMetricsMissingColumnsF11Fix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "audio_sec",
                table: "practice_answers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "filler_per100words",
                table: "practice_answers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "speech_sec",
                table: "practice_answers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "word_count",
                table: "practice_answers",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audio_sec",
                table: "practice_answers");

            migrationBuilder.DropColumn(
                name: "filler_per100words",
                table: "practice_answers");

            migrationBuilder.DropColumn(
                name: "speech_sec",
                table: "practice_answers");

            migrationBuilder.DropColumn(
                name: "word_count",
                table: "practice_answers");
        }
    }
}
