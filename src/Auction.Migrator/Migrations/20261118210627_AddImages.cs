using System;
using Auction.Migrator.Schema;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Migrator.Migrations
{
    /// <inheritdoc />
    public partial class AddImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "global_quota",
                schema: "auction",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    bytes_used = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_global_quota", x => x.id);
                    table.CheckConstraint("ck_global_quota_bytes", "bytes_used >= 0");
                    table.CheckConstraint("ck_global_quota_single_row", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "image_blobs",
                schema: "auction",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_image_blobs", x => x.id);
                    table.CheckConstraint("ck_image_blobs_dimensions", "width > 0 AND height > 0");
                    table.CheckConstraint("ck_image_blobs_sha256", "octet_length(sha256) = 32");
                    table.CheckConstraint("ck_image_blobs_size", "size_bytes BETWEEN 1 AND 5242880 AND size_bytes = octet_length(data)");
                    table.CheckConstraint("ck_image_blobs_type", "content_type IN ('image/jpeg', 'image/png', 'image/webp')");
                    table.ForeignKey(
                        name: "fk_image_blobs_users_owner_id",
                        column: x => x.owner_id,
                        principalSchema: "auction",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "upload_quotas",
                schema: "auction",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bytes_used = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    uploads_in_window = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_upload_quotas", x => x.user_id);
                    table.CheckConstraint("ck_upload_quotas_counters", "bytes_used >= 0 AND uploads_in_window >= 0");
                });

            migrationBuilder.CreateTable(
                name: "auction_images",
                schema: "auction",
                columns: table => new
                {
                    auction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    is_cover = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auction_images", x => new { x.auction_id, x.blob_id });
                    table.CheckConstraint("ck_auction_images_position", "position BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "fk_auction_images_auctions_auction_id",
                        column: x => x.auction_id,
                        principalSchema: "auction",
                        principalTable: "auctions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_auction_images_image_blobs_blob_id",
                        column: x => x.blob_id,
                        principalSchema: "auction",
                        principalTable: "image_blobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "auction",
                table: "global_quota",
                columns: new[] { "id", "bytes_used" },
                values: new object[] { 1, 0L });

            migrationBuilder.CreateIndex(
                name: "ix_auction_images_blob_id",
                schema: "auction",
                table: "auction_images",
                column: "blob_id");

            migrationBuilder.CreateIndex(
                name: "ix_image_blobs_created_at",
                schema: "auction",
                table: "image_blobs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_image_blobs_owner_id_sha256",
                schema: "auction",
                table: "image_blobs",
                columns: new[] { "owner_id", "sha256" },
                unique: true);
        
            migrationBuilder.Sql(ImageSchemaSql.Rules);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ImageSchemaSql.DropRules);

            migrationBuilder.DropTable(
                name: "auction_images",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "global_quota",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "upload_quotas",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "image_blobs",
                schema: "auction");
        }
    }
}
