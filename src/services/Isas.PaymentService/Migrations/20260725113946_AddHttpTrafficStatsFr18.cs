using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddHttpTrafficStatsFr18 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "http_traffic_stats",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    window_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    route_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status_class = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    requests = table.Column<int>(type: "integer", nullable: false),
                    sum_duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    max_duration_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_http_traffic_stats", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_http_traffic_stats_window_start",
                table: "http_traffic_stats",
                column: "window_start");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "http_traffic_stats");
        }
    }
}
