using System;
using Auction.Migrator.Schema;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Auction.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "auction");

            migrationBuilder.CreateTable(
                name: "admin_audit",
                schema: "auction",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_audit", x => x.id);
                    table.CheckConstraint("ck_admin_audit_action", "char_length(action) BETWEEN 1 AND 64");
                    table.CheckConstraint("ck_admin_audit_reason", "char_length(reason) BETWEEN 1 AND 500");
                    table.CheckConstraint("ck_admin_audit_target_type", "char_length(target_type) BETWEEN 1 AND 64");
                });

            migrationBuilder.CreateTable(
                name: "auctions",
                schema: "auction",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "draft"),
                    title = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    opening_price_cents = table.Column<long>(type: "bigint", nullable: true),
                    increment_cents = table.Column<long>(type: "bigint", nullable: false, defaultValue: 100L),
                    floor_type = table.Column<string>(type: "text", nullable: false, defaultValue: "reserve"),
                    reserve_cents = table.Column<long>(type: "bigint", nullable: true),
                    bid_entry = table.Column<string>(type: "text", nullable: false, defaultValue: "proxy"),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    current_price_cents = table.Column<long>(type: "bigint", nullable: true),
                    leader_id = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_bid_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auctions", x => x.id);
                    table.CheckConstraint("ck_auctions_bid_entry", "bid_entry IN ('proxy', 'fixed_increment')");
                    table.CheckConstraint("ck_auctions_closed_outcome", "(status = 'closed') = (outcome IS NOT NULL)");
                    table.CheckConstraint("ck_auctions_counters", "accepted_bid_count >= 0 AND version >= 0");
                    table.CheckConstraint("ck_auctions_current_price", "current_price_cents BETWEEN 1 AND 1000000000");
                    table.CheckConstraint("ck_auctions_dates", "ends_at > starts_at");
                    table.CheckConstraint("ck_auctions_description_length", "char_length(description) <= 4000");
                    table.CheckConstraint("ck_auctions_floor_type", "floor_type IN ('reserve', 'minimum_bid', 'absolute')");
                    table.CheckConstraint("ck_auctions_increment", "increment_cents BETWEEN 1 AND 1000000000");
                    table.CheckConstraint("ck_auctions_leader", "(leader_id IS NULL) = (accepted_bid_count = 0) AND (leader_id IS NULL) = (current_price_cents IS NULL)");
                    table.CheckConstraint("ck_auctions_leader_not_seller", "leader_id <> seller_id");
                    table.CheckConstraint("ck_auctions_opening_price", "opening_price_cents BETWEEN 1 AND 1000000000");
                    table.CheckConstraint("ck_auctions_outcome", "outcome IN ('sold', 'reserve_not_met', 'no_bids')");
                    table.CheckConstraint("ck_auctions_published_complete", "status = 'draft' OR (char_length(title) >= 3 AND opening_price_cents IS NOT NULL AND starts_at IS NOT NULL AND ends_at IS NOT NULL AND published_at IS NOT NULL)");
                    table.CheckConstraint("ck_auctions_reserve", "reserve_cents BETWEEN 1 AND 1000000000");
                    table.CheckConstraint("ck_auctions_reserve_rule", "status = 'draft' OR CASE WHEN floor_type = 'reserve' THEN reserve_cents IS NOT NULL AND reserve_cents >= opening_price_cents ELSE reserve_cents IS NULL END");
                    table.CheckConstraint("ck_auctions_status", "status IN ('draft', 'scheduled', 'active', 'suspended', 'closed', 'cancelled')");
                    table.CheckConstraint("ck_auctions_title_length", "char_length(title) <= 120");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "auction",
                columns: table => new
                {
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    outcome = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.actor_id, x.key });
                    table.CheckConstraint("ck_idempotency_records_key", "char_length(key) BETWEEN 1 AND 255");
                    table.CheckConstraint("ck_idempotency_records_request_hash", "octet_length(request_hash) = 32");
                });

            migrationBuilder.CreateTable(
                name: "user_limits",
                schema: "auction",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    open_drafts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    published_on = table.Column<DateOnly>(type: "date", nullable: true),
                    published_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_limits", x => x.user_id);
                    table.CheckConstraint("ck_user_limits_counters", "open_drafts >= 0 AND published_count >= 0");
                });

            migrationBuilder.CreateTable(
                name: "bids",
                schema: "auction",
                columns: table => new
                {
                    auction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    bidder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_cents = table.Column<long>(type: "bigint", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bids", x => new { x.auction_id, x.seq });
                    table.CheckConstraint("ck_bids_amount", "amount_cents BETWEEN 1 AND 1000000000");
                    table.CheckConstraint("ck_bids_seq", "seq >= 1");
                    table.CheckConstraint("ck_bids_source", "source IN ('bidder', 'proxy')");
                    table.ForeignKey(
                        name: "fk_bids_auctions_auction_id",
                        column: x => x.auction_id,
                        principalSchema: "auction",
                        principalTable: "auctions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "auction",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    auction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.CheckConstraint("ck_notifications_kind", "char_length(kind) BETWEEN 1 AND 64");
                    table.CheckConstraint("ck_notifications_reason", "char_length(reason) <= 500");
                    table.ForeignKey(
                        name: "fk_notifications_auctions_auction_id",
                        column: x => x.auction_id,
                        principalSchema: "auction",
                        principalTable: "auctions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox_events",
                schema: "auction",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    auction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    auction_version = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    xid = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_events", x => x.id);
                    table.CheckConstraint("ck_outbox_events_type", "char_length(type) BETWEEN 1 AND 64");
                    table.ForeignKey(
                        name: "fk_outbox_events_auctions_auction_id",
                        column: x => x.auction_id,
                        principalSchema: "auction",
                        principalTable: "auctions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "proxy_maxima",
                schema: "auction",
                columns: table => new
                {
                    auction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bidder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    maximum_cents = table.Column<long>(type: "bigint", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proxy_maxima", x => new { x.auction_id, x.bidder_id });
                    table.CheckConstraint("ck_proxy_maxima_maximum", "maximum_cents BETWEEN 1 AND 1000000000");
                    table.CheckConstraint("ck_proxy_maxima_sequence", "sequence >= 1");
                    table.ForeignKey(
                        name: "fk_proxy_maxima_auctions_auction_id",
                        column: x => x.auction_id,
                        principalSchema: "auction",
                        principalTable: "auctions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_target_type_target_id",
                schema: "auction",
                table: "admin_audit",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_auctions_seller_id",
                schema: "auction",
                table: "auctions",
                column: "seller_id");

            migrationBuilder.CreateIndex(
                name: "ix_auctions_status_ends_at",
                schema: "auction",
                table: "auctions",
                columns: new[] { "status", "ends_at" });

            migrationBuilder.CreateIndex(
                name: "ix_auctions_title",
                schema: "auction",
                table: "auctions",
                column: "title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_bids_bidder_id",
                schema: "auction",
                table: "bids",
                column: "bidder_id");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_created_at",
                schema: "auction",
                table: "idempotency_records",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_auction_id",
                schema: "auction",
                table: "notifications",
                column: "auction_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_created_at",
                schema: "auction",
                table: "notifications",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_auction_id",
                schema: "auction",
                table: "outbox_events",
                column: "auction_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_xid_id",
                schema: "auction",
                table: "outbox_events",
                columns: new[] { "xid", "id" },
                filter: "published_at IS NULL");

            migrationBuilder.Sql(InitialSchemaSql.Triggers);
            migrationBuilder.Sql(InitialSchemaSql.Grants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(InitialSchemaSql.DropTriggers);

            migrationBuilder.DropTable(
                name: "admin_audit",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "bids",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "outbox_events",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "proxy_maxima",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "user_limits",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "auctions",
                schema: "auction");
        }
    }
}
