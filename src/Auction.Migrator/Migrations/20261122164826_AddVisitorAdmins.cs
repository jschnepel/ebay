using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitorAdmins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_kind",
                schema: "auction",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_password",
                schema: "auction",
                table: "users");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_kind",
                schema: "auction",
                table: "users",
                sql: "kind IN ('self_registered', 'visitor_admin', 'seeded', 'simulator', 'admin')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_password",
                schema: "auction",
                table: "users",
                sql: "kind IN ('self_registered', 'visitor_admin') OR password_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_kind",
                schema: "auction",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_password",
                schema: "auction",
                table: "users");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_kind",
                schema: "auction",
                table: "users",
                sql: "kind IN ('self_registered', 'seeded', 'simulator', 'admin')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_password",
                schema: "auction",
                table: "users",
                sql: "kind = 'self_registered' OR password_hash IS NOT NULL");
        }
    }
}
