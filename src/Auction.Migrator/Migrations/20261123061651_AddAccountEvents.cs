using System;
using Auction.Migrator.Schema;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Auction.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_events",
                schema: "auction",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    type = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_events", x => x.id);
                    table.CheckConstraint("ck_account_events_detail", "char_length(detail) <= 64");
                    table.CheckConstraint("ck_account_events_email", "char_length(email) <= 254");
                    table.CheckConstraint("ck_account_events_type", "type IN ('sign_up', 'sign_in', 'sign_in_failed', 'sign_out', 'session_replaced', 'session_ended')");
                    table.ForeignKey(
                        name: "fk_account_events_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "auction",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_events_at",
                schema: "auction",
                table: "account_events",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_account_events_user_id_at",
                schema: "auction",
                table: "account_events",
                columns: new[] { "user_id", "at" });

            migrationBuilder.Sql(AccountEventSchemaSql.AppendOnly);
            migrationBuilder.Sql(AccountEventSchemaSql.Grants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AccountEventSchemaSql.RevokeGrants);
            migrationBuilder.Sql(AccountEventSchemaSql.DropAppendOnly);

            migrationBuilder.DropTable(
                name: "account_events",
                schema: "auction");
        }
    }
}
