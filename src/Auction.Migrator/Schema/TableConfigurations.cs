using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Auction.Migrator.Schema;

// Columns, keys, indexes and CHECK constraints. Triggers and grants, which EF Core can't model, are in
// InitialSchemaSql. Constraint names are part of the contract: tests and error handling match on them.

internal sealed class AuctionConfiguration : IEntityTypeConfiguration<AuctionRow>
{
    public void Configure(EntityTypeBuilder<AuctionRow> builder)
    {
        builder.ToTable("auctions", table =>
        {
            table.HasCheckConstraint("ck_auctions_status",
                "status IN ('draft', 'scheduled', 'active', 'suspended', 'closed', 'cancelled')");
            table.HasCheckConstraint("ck_auctions_floor_type", "floor_type IN ('reserve', 'minimum_bid', 'absolute')");
            table.HasCheckConstraint("ck_auctions_bid_entry", "bid_entry IN ('proxy', 'fixed_increment')");
            table.HasCheckConstraint("ck_auctions_outcome", "outcome IN ('sold', 'reserve_not_met', 'no_bids')");
            table.HasCheckConstraint("ck_auctions_closed_outcome", "(status = 'closed') = (outcome IS NOT NULL)");
            table.HasCheckConstraint("ck_auctions_title_length", "char_length(title) <= 120");
            table.HasCheckConstraint("ck_auctions_description_length", "char_length(description) <= 4000");
            table.HasCheckConstraint("ck_auctions_opening_price", Amounts.InRange("opening_price_cents"));
            table.HasCheckConstraint("ck_auctions_increment", Amounts.InRange("increment_cents"));
            table.HasCheckConstraint("ck_auctions_reserve", Amounts.InRange("reserve_cents"));
            table.HasCheckConstraint("ck_auctions_current_price", Amounts.InRange("current_price_cents"));
            table.HasCheckConstraint("ck_auctions_dates", "ends_at > starts_at");
            table.HasCheckConstraint("ck_auctions_counters", "accepted_bid_count >= 0 AND version >= 0");

            // A draft may break the listing rules; everything past draft must be a complete, valid listing.
            table.HasCheckConstraint("ck_auctions_published_complete",
                "status = 'draft' OR (char_length(title) >= 3 AND opening_price_cents IS NOT NULL " +
                "AND starts_at IS NOT NULL AND ends_at IS NOT NULL AND published_at IS NOT NULL)");
            table.HasCheckConstraint("ck_auctions_reserve_rule",
                "status = 'draft' OR CASE WHEN floor_type = 'reserve' " +
                "THEN reserve_cents IS NOT NULL AND reserve_cents >= opening_price_cents " +
                "ELSE reserve_cents IS NULL END");

            // A leader exists exactly when a bid has been accepted, and is never the seller.
            table.HasCheckConstraint("ck_auctions_leader",
                "(leader_id IS NULL) = (accepted_bid_count = 0) AND (leader_id IS NULL) = (current_price_cents IS NULL)");
            table.HasCheckConstraint("ck_auctions_leader_not_seller", "leader_id <> seller_id");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Status).HasDefaultValue("draft");
        builder.Property(a => a.Description).HasDefaultValue("");
        builder.Property(a => a.IncrementCents).HasDefaultValue(100L);
        builder.Property(a => a.FloorType).HasDefaultValue("reserve");
        builder.Property(a => a.BidEntry).HasDefaultValue("proxy");
        builder.Property(a => a.AcceptedBidCount).HasDefaultValue(0L);
        builder.Property(a => a.Version).HasDefaultValue(0L);
        builder.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(a => a.UpdatedAt).HasDefaultValueSql("now()");

        // The close worker and "ending soon" scan by status and end time; "my listings" by seller.
        builder.HasIndex(a => new { a.Status, a.EndsAt });
        builder.HasIndex(a => a.SellerId);
        builder.HasIndex(a => a.Title).HasMethod("gin").HasOperators("gin_trgm_ops");
    }
}

internal sealed class ProxyMaximumConfiguration : IEntityTypeConfiguration<ProxyMaximumRow>
{
    public void Configure(EntityTypeBuilder<ProxyMaximumRow> builder)
    {
        builder.ToTable("proxy_maxima", table =>
        {
            table.HasCheckConstraint("ck_proxy_maxima_maximum", Amounts.InRange("maximum_cents"));
            table.HasCheckConstraint("ck_proxy_maxima_sequence", "sequence >= 1");
        });
        builder.HasKey(m => new { m.AuctionId, m.BidderId });
        builder.Property(m => m.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasOne<AuctionRow>().WithMany().HasForeignKey(m => m.AuctionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BidConfiguration : IEntityTypeConfiguration<BidRow>
{
    public void Configure(EntityTypeBuilder<BidRow> builder)
    {
        builder.ToTable("bids", table =>
        {
            table.HasCheckConstraint("ck_bids_seq", "seq >= 1");
            table.HasCheckConstraint("ck_bids_amount", Amounts.InRange("amount_cents"));
            table.HasCheckConstraint("ck_bids_source", "source IN ('bidder', 'proxy')");
        });
        builder.HasKey(b => new { b.AuctionId, b.Seq });
        builder.HasOne<AuctionRow>().WithMany().HasForeignKey(b => b.AuctionId).OnDelete(DeleteBehavior.Restrict);

        // "My bids" looks bids up by bidder.
        builder.HasIndex(b => b.BidderId);
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecordRow>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecordRow> builder)
    {
        builder.ToTable("idempotency_records", table =>
        {
            table.HasCheckConstraint("ck_idempotency_records_key", "char_length(key) BETWEEN 1 AND 255");
            table.HasCheckConstraint("ck_idempotency_records_request_hash", "octet_length(request_hash) = 32");
        });
        builder.HasKey(r => new { r.ActorId, r.Key });
        builder.Property(r => r.Outcome).HasColumnType("jsonb");
        builder.Property(r => r.CreatedAt).HasDefaultValueSql("now()");

        // The 24-hour sweep deletes by age.
        builder.HasIndex(r => r.CreatedAt);
    }
}

internal sealed class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEventRow>
{
    public void Configure(EntityTypeBuilder<OutboxEventRow> builder)
    {
        builder.ToTable("outbox_events", table =>
            table.HasCheckConstraint("ck_outbox_events_type", "char_length(type) BETWEEN 1 AND 64"));
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();
        builder.Property(e => e.Payload).HasColumnType("jsonb");
        builder.Property(e => e.Xid).HasColumnType("xid8").HasDefaultValueSql("pg_current_xact_id()");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.HasOne<AuctionRow>().WithMany().HasForeignKey(e => e.AuctionId).OnDelete(DeleteBehavior.Restrict);

        // The relay reads unpublished events in commit order.
        builder.HasIndex(e => new { e.Xid, e.Id }).HasFilter("published_at IS NULL");
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<NotificationRow>
{
    public void Configure(EntityTypeBuilder<NotificationRow> builder)
    {
        builder.ToTable("notifications", table =>
        {
            table.HasCheckConstraint("ck_notifications_kind", "char_length(kind) BETWEEN 1 AND 64");
            table.HasCheckConstraint("ck_notifications_reason", "char_length(reason) <= 500");
        });
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).UseIdentityAlwaysColumn();
        builder.Property(n => n.CreatedAt).HasDefaultValueSql("now()");
        builder.HasOne<AuctionRow>().WithMany().HasForeignKey(n => n.AuctionId).OnDelete(DeleteBehavior.Restrict);

        // The bell shows a user's newest notices first.
        builder.HasIndex(n => new { n.UserId, n.CreatedAt }).IsDescending(false, true);
    }
}

internal sealed class AdminAuditConfiguration : IEntityTypeConfiguration<AdminAuditRow>
{
    public void Configure(EntityTypeBuilder<AdminAuditRow> builder)
    {
        builder.ToTable("admin_audit", table =>
        {
            table.HasCheckConstraint("ck_admin_audit_action", "char_length(action) BETWEEN 1 AND 64");
            table.HasCheckConstraint("ck_admin_audit_target_type", "char_length(target_type) BETWEEN 1 AND 64");
            table.HasCheckConstraint("ck_admin_audit_reason", "char_length(reason) BETWEEN 1 AND 500");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityAlwaysColumn();
        builder.Property(a => a.At).HasDefaultValueSql("now()");
        builder.HasIndex(a => new { a.TargetType, a.TargetId });
    }
}

internal sealed class UserLimitsConfiguration : IEntityTypeConfiguration<UserLimitsRow>
{
    public void Configure(EntityTypeBuilder<UserLimitsRow> builder)
    {
        builder.ToTable("user_limits", table =>
            table.HasCheckConstraint("ck_user_limits_counters", "open_drafts >= 0 AND published_count >= 0"));
        builder.HasKey(l => l.UserId);
        builder.Property(l => l.OpenDrafts).HasDefaultValue(0);
        builder.Property(l => l.PublishedCount).HasDefaultValue(0);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<UserRow>
{
    public void Configure(EntityTypeBuilder<UserRow> builder)
    {
        builder.ToTable("users", table =>
        {
            table.HasCheckConstraint("ck_users_kind", "kind IN ('self_registered', 'visitor_admin', 'seeded', 'simulator', 'admin')");
            table.HasCheckConstraint("ck_users_email", "char_length(email) BETWEEN 3 AND 254");
            table.HasCheckConstraint("ck_users_display_name", "char_length(display_name) BETWEEN 1 AND 50");
            // Only accounts visitors created may sign in without a password (and only under DEMO_MODE).
            table.HasCheckConstraint("ck_users_password", "kind IN ('self_registered', 'visitor_admin') OR password_hash IS NOT NULL");
        });
        builder.HasKey(u => u.Id);
        builder.HasIndex(u => u.NormalizedEmail).IsUnique();
        builder.Property(u => u.Kind).HasDefaultValue("self_registered");
        builder.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
    }
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<SessionRow>
{
    public void Configure(EntityTypeBuilder<SessionRow> builder)
    {
        builder.ToTable("sessions");
        builder.HasKey(s => s.Id);
        builder.HasOne<UserRow>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        // Revoking finds an account's live sessions.
        builder.HasIndex(s => s.UserId).HasFilter("revoked_at IS NULL");
        builder.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
    }
}

internal sealed class ImageBlobConfiguration : IEntityTypeConfiguration<ImageBlobRow>
{
    public void Configure(EntityTypeBuilder<ImageBlobRow> builder)
    {
        builder.ToTable("image_blobs", table =>
        {
            table.HasCheckConstraint("ck_image_blobs_type", "content_type IN ('image/jpeg', 'image/png', 'image/webp')");
            table.HasCheckConstraint("ck_image_blobs_size", "size_bytes BETWEEN 1 AND 5242880 AND size_bytes = octet_length(data)");
            table.HasCheckConstraint("ck_image_blobs_sha256", "octet_length(sha256) = 32");
            table.HasCheckConstraint("ck_image_blobs_dimensions", "width > 0 AND height > 0");
        });
        builder.HasKey(b => b.Id);
        builder.HasOne<UserRow>().WithMany().HasForeignKey(b => b.OwnerId).OnDelete(DeleteBehavior.Restrict);
        // The same bytes from the same owner are one blob, so a retried upload returns it.
        builder.HasIndex(b => new { b.OwnerId, b.Sha256 }).IsUnique();
        builder.HasIndex(b => b.CreatedAt);
        builder.Property(b => b.CreatedAt).HasDefaultValueSql("now()");
    }
}

internal sealed class AuctionImageConfiguration : IEntityTypeConfiguration<AuctionImageRow>
{
    public void Configure(EntityTypeBuilder<AuctionImageRow> builder)
    {
        builder.ToTable("auction_images", table => table.HasCheckConstraint("ck_auction_images_position", "position BETWEEN 0 AND 3"));
        builder.HasKey(i => new { i.AuctionId, i.BlobId });
        // Deleting a draft takes its links with it; a blob that is still linked can't be deleted (the sweep
        // and an attach serialize on this key instead of one silently undoing the other).
        builder.HasOne<AuctionRow>().WithMany().HasForeignKey(i => i.AuctionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ImageBlobRow>().WithMany().HasForeignKey(i => i.BlobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(i => i.BlobId);
    }
}

internal sealed class UploadQuotaConfiguration : IEntityTypeConfiguration<UploadQuotaRow>
{
    public void Configure(EntityTypeBuilder<UploadQuotaRow> builder)
    {
        builder.ToTable("upload_quotas", table =>
            table.HasCheckConstraint("ck_upload_quotas_counters", "bytes_used >= 0 AND uploads_in_window >= 0"));
        builder.HasKey(q => q.UserId);
        builder.Property(q => q.BytesUsed).HasDefaultValue(0L);
        builder.Property(q => q.WindowStart).HasDefaultValueSql("now()");
        builder.Property(q => q.UploadsInWindow).HasDefaultValue(0);
    }
}

internal sealed class GlobalQuotaConfiguration : IEntityTypeConfiguration<GlobalQuotaRow>
{
    public void Configure(EntityTypeBuilder<GlobalQuotaRow> builder)
    {
        builder.ToTable("global_quota", table =>
        {
            table.HasCheckConstraint("ck_global_quota_single_row", "id = 1");
            table.HasCheckConstraint("ck_global_quota_bytes", "bytes_used >= 0");
        });
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).ValueGeneratedNever();
        builder.HasData(new GlobalQuotaRow { Id = 1, BytesUsed = 0 });
    }
}

internal sealed class DemoResetConfiguration : IEntityTypeConfiguration<DemoResetRow>
{
    public void Configure(EntityTypeBuilder<DemoResetRow> builder)
    {
        builder.ToTable("demo_resets", table => table.HasCheckConstraint("ck_demo_resets_requested_by", "char_length(requested_by) BETWEEN 1 AND 50"));
        builder.HasKey(r => r.Id);
        // At most one request waits at a time: see DemoResetSchemaSql.OnePending.
        builder.HasIndex(r => r.CompletedAt).HasDatabaseName("ix_demo_resets_completed_at");
    }
}
