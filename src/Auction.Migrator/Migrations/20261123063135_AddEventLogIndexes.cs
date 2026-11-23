using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class AddEventLogIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "ix_auctions_closed_at",
                schema: "auction",
                table: "auctions",
                column: "closed_at");

            migrationBuilder.CreateIndex(
                name: "ix_auctions_published_at",
                schema: "auction",
                table: "auctions",
                column: "published_at");

            migrationBuilder.CreateIndex(
                name: "ix_auctions_seller_id_published_at",
                schema: "auction",
                table: "auctions",
                columns: new[] { "seller_id", "published_at" });

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_at",
                schema: "auction",
                table: "admin_audit",
                column: "at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_events_created_at_brin",
                schema: "auction",
                table: "outbox_events");

            migrationBuilder.DropIndex(
                name: "ix_bids_accepted_at_brin",
                schema: "auction",
                table: "bids");

            migrationBuilder.DropIndex(
                name: "ix_auctions_closed_at",
                schema: "auction",
                table: "auctions");

            migrationBuilder.DropIndex(
                name: "ix_auctions_published_at",
                schema: "auction",
                table: "auctions");

            migrationBuilder.DropIndex(
                name: "ix_auctions_seller_id_published_at",
                schema: "auction",
                table: "auctions");

            migrationBuilder.DropIndex(
                name: "ix_admin_audit_at",
                schema: "auction",
                table: "admin_audit");
        }
    }
}
