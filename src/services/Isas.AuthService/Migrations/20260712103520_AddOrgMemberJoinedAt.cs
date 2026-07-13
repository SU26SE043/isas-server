using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.AuthService.Migrations
{
    /// <inheritdoc />
    public partial class AddOrgMemberJoinedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A6b — cột joined_at (thời điểm gia nhập org thật). Backfill hàng cũ = now() lúc apply
            // (Postgres). defaultValueSql chỉ đặt trong migration này (không vào model/snapshot) → app
            // luôn set JoinedAt tường minh khi tạo member; has-pending-model-changes = No changes.
            migrationBuilder.AddColumn<DateTime>(
                name: "joined_at",
                table: "org_members",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "joined_at",
                table: "org_members");
        }
    }
}
