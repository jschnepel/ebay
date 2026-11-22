using System;
using Auction.Migrator.Schema;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoResets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "demo_resets",
                schema: "auction",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_demo_resets", x => x.id);
                    table.CheckConstraint("ck_demo_resets_requested_by", "char_length(requested_by) BETWEEN 1 AND 50");
                });

            migrationBuilder.CreateIndex(
                name: "ix_demo_resets_completed_at",
                schema: "auction",
                table: "demo_resets",
                column: "completed_at");

            migrationBuilder.Sql(DemoResetSchemaSql.OnePending);
            migrationBuilder.Sql(DemoResetSchemaSql.Grants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DemoResetSchemaSql.RevokeGrants);
            migrationBuilder.Sql(DemoResetSchemaSql.DropOnePending);

            migrationBuilder.DropTable(
                name: "demo_resets",
                schema: "auction");
        }
    }
}
