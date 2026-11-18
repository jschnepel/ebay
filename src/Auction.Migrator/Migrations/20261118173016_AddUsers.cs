using System;
using Auction.Migrator.Schema;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class AddUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                schema: "auction",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    normalized_email = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false, defaultValue: "self_registered"),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_display_name", "char_length(display_name) BETWEEN 1 AND 50");
                    table.CheckConstraint("ck_users_email", "char_length(email) BETWEEN 3 AND 254");
                    table.CheckConstraint("ck_users_kind", "kind IN ('self_registered', 'seeded', 'simulator', 'admin')");
                    table.CheckConstraint("ck_users_password", "kind = 'self_registered' OR password_hash IS NOT NULL");
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_email",
                schema: "auction",
                table: "users",
                column: "normalized_email",
                unique: true);

            migrationBuilder.Sql(AccountsSchemaSql.Grants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "users",
                schema: "auction");
        }
    }
}
