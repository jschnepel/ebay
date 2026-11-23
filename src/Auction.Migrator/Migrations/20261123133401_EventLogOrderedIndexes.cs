using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class EventLogOrderedIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_events_created_at_brin",
                schema: "auction",
                table: "outbox_events");

            migrationBuilder.DropIndex(
                name: "ix_bids_accepted_at_brin",
                schema: "auction",
                table: "bids");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_cancelled_created_at",
                schema: "auction",
                table: "outbox_events",
                column: "created_at",
                filter: "type = 'auction.cancelled'");

            migrationBuilder.CreateIndex(
                name: "ix_bids_accepted_at",
                schema: "auction",
                table: "bids",
                column: "accepted_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_events_cancelled_created_at",
                schema: "auction",
                table: "outbox_events");

            migrationBuilder.DropIndex(
                name: "ix_bids_accepted_at",
                schema: "auction",
                table: "bids");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_created_at_brin",
                schema: "auction",
                table: "outbox_events",
                column: "created_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_bids_accepted_at_brin",
                schema: "auction",
                table: "bids",
                column: "accepted_at")
                .Annotation("Npgsql:IndexMethod", "brin");
        }
    }
}
