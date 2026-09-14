using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.InterviewService.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeFocusEventsB2c : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "focus_tracking_enabled",
                table: "practice_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "practice_focus_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signal_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    note = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practice_focus_events", x => x.id);
                    table.CheckConstraint("ck_practice_focus_events_signal_type", "signal_type IN ('tab_switch', 'paste', 'focus_lost')");
                    table.ForeignKey(
                        name: "fk_practice_focus_events_practice_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "practice_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_practice_focus_events_session_id_signal_type",
                table: "practice_focus_events",
                columns: new[] { "session_id", "signal_type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "practice_focus_events");

            migrationBuilder.DropColumn(
                name: "focus_tracking_enabled",
                table: "practice_sessions");
        }
    }
}
