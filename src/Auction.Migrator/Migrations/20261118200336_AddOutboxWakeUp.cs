using Auction.Migrator.Schema;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxWakeUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(OutboxSchemaSql.WakeUp);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(OutboxSchemaSql.DropWakeUp);
        }
    }
}
