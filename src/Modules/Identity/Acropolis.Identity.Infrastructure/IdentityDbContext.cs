using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Identity.Infrastructure;

public sealed class ChannelUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsDisabled { get; set; }
    public bool UsersManage { get; set; }
    public bool ContentManage { get; set; }
    public bool RevalidationRequired { get; set; }
    public DateTimeOffset? RevalidatedUtc { get; set; }
    public string SecurityVersion { get; set; } = Guid.NewGuid().ToString("N");
    public List<UserLevel> Levels { get; set; } = [];
}
public sealed class UserLevel
{
    public Guid UserId { get; set; }
    public string Level { get; set; } = string.Empty;
}
public sealed class StoredSession
{
    public string Id { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string SecurityVersion { get; set; } = string.Empty;
    public byte[] Ticket { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
}
public sealed class IdentityFlow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? ConsumedUtc { get; set; }
}
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid FlowId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
}
public sealed class UserAudit
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public Guid UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Changes { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
}
public sealed class BootstrapState
{
    public int Id { get; set; }
    public bool Completed { get; set; }
}
public sealed partial class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : IdentityUserContext<ChannelUser, Guid>(options)
{
    public const string Schema = "identity";
    public const string HistoryTable = "__EFMigrationsHistory";
    public DbSet<StoredSession> Sessions => Set<StoredSession>();
    public DbSet<IdentityFlow> Flows => Set<IdentityFlow>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<UserAudit> Audit => Set<UserAudit>();
    public DbSet<BootstrapState> Bootstrap => Set<BootstrapState>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        ConfigureMfa(builder);
        builder.HasDefaultSchema(Schema);
        builder.HasPostgresExtension(Schema, "pg_trgm");
        builder.Entity<ChannelUser>(entity =>
        {
            entity.ToTable("Users");
            entity.Property(x => x.DisplayName).HasMaxLength(100);
            entity.Property(x => x.SecurityVersion).HasMaxLength(32);
            entity.Property(x => x.ConcurrencyStamp).HasMaxLength(64);
            entity.HasIndex(x => x.NormalizedEmail).IsUnique();
            entity.HasIndex(x => x.NormalizedEmail, "IX_Users_SearchEmail")
                .HasMethod("gin").HasOperators("identity.gin_trgm_ops");
            entity.HasIndex(x => x.DisplayName, "IX_Users_SearchName")
                .HasMethod("gin").HasOperators("identity.gin_trgm_ops");
            entity.HasMany(x => x.Levels).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("UserClaims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("UserLogins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("UserTokens");
        builder.Entity<UserLevel>(entity => { entity.ToTable("UserLevels"); entity.HasKey(x => new { x.UserId, x.Level }); entity.Property(x => x.Level).HasMaxLength(32); });
        builder.Entity<StoredSession>(entity =>
        {
            entity.HasKey(x => x.Id); entity.Property(x => x.Id).HasMaxLength(64); entity.Property(x => x.SecurityVersion).HasMaxLength(32);
            entity.HasIndex(x => x.UserId); entity.HasIndex(x => x.ExpiresUtc);
            entity.HasOne<ChannelUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<IdentityFlow>(entity =>
        {
            entity.Property(x => x.TokenHash).HasMaxLength(64); entity.Property(x => x.Purpose).HasMaxLength(32);
            entity.HasIndex(x => x.TokenHash).IsUnique(); entity.HasIndex(x => new { x.UserId, x.Purpose });
            entity.HasOne<ChannelUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<OutboxMessage>(entity =>
        {
            entity.Property(x => x.Status).HasMaxLength(32); entity.Property(x => x.LeaseOwner).HasMaxLength(32); entity.HasIndex(x => new { x.Status, x.NextAttemptUtc });
            entity.HasOne<IdentityFlow>().WithMany().HasForeignKey(x => x.FlowId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<UserAudit>(entity => { entity.Property(x => x.Action).HasMaxLength(64); entity.Property(x => x.Changes).HasMaxLength(1024); });
        builder.Entity<BootstrapState>().HasKey(x => x.Id);
    }
}
