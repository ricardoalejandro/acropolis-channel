using Microsoft.EntityFrameworkCore;
namespace Acropolis.Identity.Infrastructure;

public sealed class MfaCredential
{
    public Guid UserId { get; set; }
    public string ProtectedKey { get; set; } = "";
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
}
public sealed class MfaChallenge
{
    public string Id { get; set; } = "";
    public Guid UserId { get; set; }
    public string SecurityVersion { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string? ProtectedKey { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedUtc { get; set; }
}
public sealed class MfaRecoveryCode
{
    public Guid UserId { get; set; }
    public string Hash { get; set; } = "";
}
public sealed class MfaProof
{
    public Guid UserId { get; set; }
    public string Hash { get; set; } = "";
    public DateTimeOffset ExpiresUtc { get; set; }
}
public sealed partial class IdentityDbContext
{
    public DbSet<MfaCredential> MfaCredentials => Set<MfaCredential>();
    public DbSet<MfaChallenge> MfaChallenges => Set<MfaChallenge>();
    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();
    public DbSet<MfaProof> MfaProofs => Set<MfaProof>();
    private static void ConfigureMfa(ModelBuilder builder)
    {
        builder.Entity<MfaCredential>(e => { e.HasKey(x => x.UserId); e.HasOne<ChannelUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
        builder.Entity<MfaChallenge>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(64); e.Property(x => x.SecurityVersion).HasMaxLength(32); e.Property(x => x.Purpose).HasMaxLength(16);
            e.HasIndex(x => x.ExpiresUtc); e.HasIndex(x => x.UserId); e.HasOne<ChannelUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MfaRecoveryCode>(e => { e.HasKey(x => new { x.UserId, x.Hash }); e.Property(x => x.Hash).HasMaxLength(64); e.HasOne<ChannelUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
        builder.Entity<MfaProof>(e => { e.HasKey(x => new { x.UserId, x.Hash }); e.Property(x => x.Hash).HasMaxLength(64); e.HasIndex(x => x.ExpiresUtc); e.HasOne<ChannelUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
    }
}
