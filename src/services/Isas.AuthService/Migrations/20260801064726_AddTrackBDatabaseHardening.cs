using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isas.AuthService.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackBDatabaseHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_organizations_tax_code",
                table: "organizations",
                column: "tax_code",
                unique: true,
                filter: "tax_code IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_org_members_org_role",
                table: "org_members",
                sql: "org_role IN ('OrgAdmin', 'HrMember')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_organizations_tax_code",
                table: "organizations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_org_members_org_role",
                table: "org_members");
        }
    }
}
