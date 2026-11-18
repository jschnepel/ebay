using Auction.Migrator.Schema;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class AddModerationAndNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "auction_version",
                schema: "auction",
                table: "notifications",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(ModerationSchemaSql.Grants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ModerationSchemaSql.RevokeGrants);

            migrationBuilder.DropColumn(
                name: "auction_version",
                schema: "auction",
                table: "notifications");
        }
    }
}
