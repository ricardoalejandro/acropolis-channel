using Microsoft.EntityFrameworkCore;

namespace Acropolis.Subscriptions.Infrastructure;

public sealed class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Plan { get; set; } = "free_beta";
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset StartsUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public DateTimeOffset ActivatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? CancelledUtc { get; set; }
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
}
public sealed class SubscriptionAudit
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid UserId { get; set; }
    public Guid ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? BeforeStatus { get; set; }
    public string AfterStatus { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public string? BeforePlan { get; set; }
    public string? AfterPlan { get; set; }
    public DateTimeOffset? BeforeStartsUtc { get; set; }
    public DateTimeOffset? AfterStartsUtc { get; set; }
    public DateTimeOffset? BeforeExpiresUtc { get; set; }
    public DateTimeOffset? AfterExpiresUtc { get; set; }
}
public sealed class SubscriptionsDbContext(DbContextOptions<SubscriptionsDbContext> options) : DbContext(options)
{
    public const string Schema = "subscriptions";
    public const string HistoryTable = "__EFMigrationsHistory";
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionAudit> Audit => Set<SubscriptionAudit>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);
        builder.Entity<Subscription>(entity =>
        {
            entity.ToTable("Subscriptions", table =>
            {
                table.HasCheckConstraint("CK_Subscriptions_Status", "\"Status\" IN ('active','cancelled','suspended')");
                table.HasCheckConstraint("CK_Subscriptions_Plan", "\"Plan\" IN ('free_beta','probationismo','annual')");
                table.HasCheckConstraint("CK_Subscriptions_Terms", "(\"Plan\"='free_beta' AND \"ExpiresUtc\" IS NULL) OR (\"Plan\" IN ('probationismo','annual') AND \"ExpiresUtc\" IS NOT NULL AND \"ExpiresUtc\">\"StartsUtc\")");
            });
            entity.HasKey(x => x.Id); entity.HasIndex(x => x.UserId).IsUnique();
            entity.Property(x => x.Plan).HasMaxLength(32); entity.Property(x => x.Status).HasMaxLength(16);
            entity.Property(x => x.Version).HasMaxLength(64).IsConcurrencyToken();
            entity.HasIndex(x => new { x.Status, x.UpdatedUtc, x.Id });
            entity.HasIndex(x => new { x.UpdatedUtc, x.Id });
        });
        builder.Entity<SubscriptionAudit>(entity =>
        {
            entity.ToTable("Audit"); entity.HasKey(x => x.Id);
            entity.Property(x => x.BeforePlan).HasMaxLength(32); entity.Property(x => x.AfterPlan).HasMaxLength(32);
            entity.Property(x => x.Action).HasMaxLength(64); entity.Property(x => x.BeforeStatus).HasMaxLength(16);
            entity.Property(x => x.AfterStatus).HasMaxLength(16); entity.Property(x => x.Reason).HasMaxLength(200);
            entity.HasIndex(x => new { x.CreatedUtc, x.Id }); entity.HasIndex(x => new { x.SubscriptionId, x.CreatedUtc });
            entity.HasIndex(x => new { x.UserId, x.CreatedUtc });
            entity.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
