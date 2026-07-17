using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditColumnsAndTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DB14 — product_packages.type int→varchar(20) (GEN-2 enum lưu string). AlterColumn do EF scaffold
            // KHÔNG có USING → Postgres không tự cast int→text ⇒ hand-write ALTER ... USING map giá trị int cũ
            // sang tên enum (PackageType: 1=OneTime, 2=Subscription). Postgres-only (migration target Postgres;
            // test SQLite dùng EnsureCreated dựng từ model nên KHÔNG chạy data-migration này).
            migrationBuilder.Sql(
                "ALTER TABLE product_packages ALTER COLUMN type TYPE character varying(20) " +
                "USING (CASE type WHEN 1 THEN 'OneTime' WHEN 2 THEN 'Subscription' END);");

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "product_packages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "Pending");

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "credit_reservations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "product_packages");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "credit_reservations");

            // DB14 — reverse: varchar(20)→int, map tên enum về giá trị int (USING ...::integer).
            migrationBuilder.Sql(
                "ALTER TABLE product_packages ALTER COLUMN type TYPE integer " +
                "USING (CASE type WHEN 'OneTime' THEN 1 WHEN 'Subscription' THEN 2 END)::integer;");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "orders",
                type: "text",
                nullable: false,
                defaultValue: "Pending",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValue: "Pending");
        }
    }
}
