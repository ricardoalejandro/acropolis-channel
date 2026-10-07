using Microsoft.EntityFrameworkCore;

namespace Acropolis.Subscriptions.Infrastructure;

public sealed class SubscriptionNotification
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid UserId { get; set; }
    public Guid? SourceAuditId { get; set; }
    public Guid TermGeneration { get; set; }
    public string DeduplicationKey { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public DateTimeOffset StartsUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
}
public sealed class SubscriptionNotificationDeliveryState
{
    public int Id { get; set; } = 1;
    public DateTimeOffset NextSubmissionUtc { get; set; }
}
public sealed partial class SubscriptionsDbContext
{
    public DbSet<SubscriptionNotification> Notifications => Set<SubscriptionNotification>();
    public DbSet<SubscriptionNotificationDeliveryState> NotificationDeliveryState => Set<SubscriptionNotificationDeliveryState>();
    private static void ConfigureNotifications(ModelBuilder builder)
    {
        builder.Entity<SubscriptionNotification>(entity =>
        {
            entity.ToTable("NotificationOutbox", table =>
            {
                table.HasCheckConstraint("CK_NotificationOutbox_Kind", "\"Kind\" IN ('assigned','renewed','expiring')");
                table.HasCheckConstraint("CK_NotificationOutbox_Status", "\"Status\" IN ('pending','sending','sent','failed','cancelled')");
                table.HasCheckConstraint("CK_NotificationOutbox_Attempts", "\"Attempts\" BETWEEN 0 AND 5");
                table.HasCheckConstraint("CK_NotificationOutbox_Source", "(\"Kind\"='expiring' AND \"SourceAuditId\" IS NULL) OR (\"Kind\" IN ('assigned','renewed') AND \"SourceAuditId\" IS NOT NULL)");
                table.HasCheckConstraint("CK_NotificationOutbox_Reminder", "\"Kind\"<>'expiring' OR \"ExpiresUtc\" IS NOT NULL");
                table.HasCheckConstraint("CK_NotificationOutbox_Lease", "(\"Status\"='sending' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseExpiresUtc\" IS NOT NULL) OR (\"Status\"<>'sending' AND \"LeaseOwner\" IS NULL AND \"LeaseExpiresUtc\" IS NULL)");
                table.HasCheckConstraint("CK_NotificationOutbox_Terms", "(\"Plan\"='free_beta' AND \"ExpiresUtc\" IS NULL) OR (\"Plan\" IN ('probationismo','annual') AND \"ExpiresUtc\" IS NOT NULL AND \"ExpiresUtc\">\"StartsUtc\")");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DeduplicationKey).HasMaxLength(96);
            entity.Property(x => x.Kind).HasMaxLength(16); entity.Property(x => x.Plan).HasMaxLength(32);
            entity.Property(x => x.Status).HasMaxLength(16); entity.Property(x => x.LeaseOwner).HasMaxLength(32);
            entity.HasIndex(x => x.DeduplicationKey).IsUnique();
            entity.HasIndex(x => x.SourceAuditId).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextAttemptUtc, x.Id });
            entity.HasIndex(x => new { x.SubscriptionId, x.Kind, x.TermGeneration });
            entity.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SubscriptionAudit>().WithMany().HasForeignKey(x => x.SourceAuditId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SubscriptionNotificationDeliveryState>(entity =>
        {
            entity.ToTable("NotificationDeliveryState", table => table.HasCheckConstraint("CK_NotificationDeliveryState_Id", "\"Id\"=1"));
            entity.HasKey(x => x.Id); entity.Property(x => x.Id).ValueGeneratedNever();
        });
    }
}
