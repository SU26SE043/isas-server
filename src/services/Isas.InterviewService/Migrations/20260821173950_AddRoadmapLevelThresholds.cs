using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadmapLevelThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "roadmap_level_thresholds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    threshold_pct = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roadmap_level_thresholds", x => x.id);
                    table.CheckConstraint("ck_roadmap_level_thresholds_pct_range", "threshold_pct >= 0 AND threshold_pct <= 100");
                });

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_level_thresholds_level",
                table: "roadmap_level_thresholds",
                column: "level",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "roadmap_level_thresholds");
        }
    }
}
