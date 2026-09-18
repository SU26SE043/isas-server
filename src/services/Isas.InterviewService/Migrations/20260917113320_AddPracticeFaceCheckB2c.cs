using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeFaceCheckB2c : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_practice_focus_events_signal_type",
                table: "practice_focus_events");

            migrationBuilder.CreateTable(
                name: "practice_face_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    captured_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practice_face_images", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_practice_focus_events_signal_type",
                table: "practice_focus_events",
                sql: "signal_type IN ('tab_switch', 'paste', 'focus_lost', 'no_face', 'multiple_faces')");

            migrationBuilder.CreateIndex(
                name: "ix_practice_face_images_captured_at",
                table: "practice_face_images",
                column: "captured_at");

            migrationBuilder.CreateIndex(
                name: "ix_practice_face_images_session_id",
                table: "practice_face_images",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_practice_face_images_storage_key",
                table: "practice_face_images",
                column: "storage_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "practice_face_images");

            migrationBuilder.DropCheckConstraint(
                name: "ck_practice_focus_events_signal_type",
                table: "practice_focus_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_practice_focus_events_signal_type",
                table: "practice_focus_events",
                sql: "signal_type IN ('tab_switch', 'paste', 'focus_lost')");
        }
    }
}
