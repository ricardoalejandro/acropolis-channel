using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acropolis.Migrations;

public sealed class IdentityOperations(IdentityDbContext database, TimeProvider clock)
{
    public async Task BootstrapAsync(string email, CancellationToken token)
    {
        if (!IdentityRules.ValidEmail(email)) throw new InvalidOperationException("Invalid administrator email.");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityService.AdministrationLockKey})", token);
        var state = await database.Bootstrap.SingleOrDefaultAsync(x => x.Id == 1, token);
        if (state?.Completed == true || await database.Users.AnyAsync(x => x.UsersManage, token)) throw new InvalidOperationException("Administrator bootstrap is already complete.");
        var userId = await database.Users.Where(x => x.Email == email).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        if (userId is null) throw new InvalidOperationException("The exact administrator account must be registered and confirmed.");
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityService.UserLock(userId.Value)})", token);
        var user = await database.Users.SingleOrDefaultAsync(x => x.Id == userId.Value, token);
        if (user is null || !user.EmailConfirmed || user.IsDisabled || user.RevalidationRequired) throw new InvalidOperationException("The exact administrator account must be registered and confirmed.");
        user.UsersManage = true;
        user.SecurityVersion = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await database.Sessions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(token);
        if (state is null) database.Bootstrap.Add(new BootstrapState { Id = 1, Completed = true });
        else state.Completed = true;
        database.Audit.Add(new UserAudit { Id = Guid.NewGuid(), ActorId = user.Id, UserId = user.Id, Action = "admin.bootstrap", Changes = "Users.Manage granted by explicit administrator CLI.", CreatedUtc = clock.GetUtcNow() });
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
    public async Task InvalidateRecoveryAsync(CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityService.AdministrationLockKey})", token);
        var now = clock.GetUtcNow();
        await database.Sessions.ExecuteDeleteAsync(token);
        await database.MfaChallenges.ExecuteDeleteAsync(token);
        await database.MfaProofs.ExecuteDeleteAsync(token);
        await database.MfaRecoveryCodes.ExecuteDeleteAsync(token);
        await database.MfaCredentials.ExecuteDeleteAsync(token);
        await database.Flows.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ConsumedUtc, now), token);
        await database.Outbox.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "cancelled").SetProperty(x => x.Payload, ""), token);
        await database.Users.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevalidationRequired, true).SetProperty(x => x.TwoFactorEnabled, false), token);
        await transaction.CommitAsync(token);
    }
    public async Task RevalidateAsync(string email, bool grantAdministrator, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityService.AdministrationLockKey})", token);
        var user = await database.Users.Include(x => x.Levels).SingleOrDefaultAsync(x => x.Email == email, token);
        if (user is null || !user.RevalidationRequired) throw new InvalidOperationException("The exact account must require revalidation.");
        // Operator approval covers permissions; old passwords are always invalidated.
        await database.MfaChallenges.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(token);
        await database.MfaProofs.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(token);
        await database.MfaRecoveryCodes.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(token);
        await database.MfaCredentials.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(token);
        user.TwoFactorEnabled = false;
        user.RevalidationRequired = false;
        user.RevalidatedUtc = clock.GetUtcNow();
        user.UsersManage = grantAdministrator;
        user.ContentManage = false;
        user.IsDisabled = false;
        user.PasswordHash = null;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.EmailConfirmed = false;
        user.SecurityVersion = Guid.NewGuid().ToString("N");
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        database.RemoveRange(user.Levels);
        user.Levels = [new UserLevel { UserId = user.Id, Level = "Externo" }];
        database.Audit.Add(new UserAudit { Id = Guid.NewGuid(), ActorId = Guid.Empty, UserId = user.Id, Action = "account.revalidated", Changes = JsonSerializer.Serialize(new { administrator = grantAdministrator, credentialsReset = true }), CreatedUtc = clock.GetUtcNow() });
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task SetContentManagerAsync(string email, bool granted, CancellationToken token)
    {
        if (!IdentityRules.ValidEmail(email)) throw new InvalidOperationException("Invalid editorial account email.");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityService.AdministrationLockKey})", token);
        var user = await database.Users.FromSqlInterpolated($"SELECT * FROM identity.\"Users\" WHERE \"Email\"={email} FOR UPDATE").SingleOrDefaultAsync(token);
        if (user is null || (granted && (!user.EmailConfirmed || user.IsDisabled || user.RevalidationRequired)))
            throw new InvalidOperationException("The exact editorial account must be registered, confirmed and active.");
        if (user.ContentManage == granted) return;
        user.ContentManage = granted;
        user.SecurityVersion = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await database.Sessions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(token);
        database.Audit.Add(new UserAudit
        {
            Id = Guid.NewGuid(),
            ActorId = Guid.Empty,
            UserId = user.Id,
            Action = granted ? "content.permission.granted" : "content.permission.revoked",
            Changes = "Content.Manage changed by explicit operator CLI.",
            CreatedUtc = clock.GetUtcNow()
        });
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<int> PruneAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var expired = await database.Sessions.Where(x => x.ExpiresUtc <= now).OrderBy(x => x.ExpiresUtc).Take(1000).ExecuteDeleteAsync(token);
        expired += await database.MfaChallenges.Where(x => x.ExpiresUtc <= now || x.ConsumedUtc != null).OrderBy(x => x.ExpiresUtc).Take(1000).ExecuteDeleteAsync(token);
        expired += await database.MfaProofs.Where(x => x.ExpiresUtc <= now).OrderBy(x => x.ExpiresUtc).Take(1000).ExecuteDeleteAsync(token);
        // Flow deletion cascades terminal/expired outbox payloads; active flows remain usable.
        return expired + await database.Flows.Where(x => x.ExpiresUtc < now.AddDays(-7)).OrderBy(x => x.ExpiresUtc).Take(1000).ExecuteDeleteAsync(token);
    }
    public async Task SeedQaAsync(int count, string password, CancellationToken token)
    {
        var connection = new NpgsqlConnectionStringBuilder(database.Database.GetConnectionString());
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Testing" || connection.Database is null || !System.Text.RegularExpressions.Regex.IsMatch(connection.Database, "^acropolis_test_[a-z0-9_]+$") || count is < 1 or > 100000 || !IdentityRules.ValidPassword(password))
            throw new InvalidOperationException("QA seed is restricted to guarded test databases.");
        var hasher = new PasswordHasher<ChannelUser>();
        var hash = hasher.HashPassword(new ChannelUser(), password);
        var existing = (await database.Users.AsNoTracking().Where(x => x.NormalizedEmail!.StartsWith("QA-LOAD-")).Select(x => x.NormalizedEmail!).ToArrayAsync(token)).ToHashSet(StringComparer.Ordinal);
        for (var start = 0; start < count; start += 1000)
        {
            for (var offset = start; offset < Math.Min(start + 1000, count); offset++)
            {
                var email = $"qa-load-{offset:D6}@example.test";
                if (existing.Contains(email.ToUpperInvariant())) continue;
                var user = new ChannelUser { Id = Guid.NewGuid(), UserName = email, NormalizedUserName = email.ToUpperInvariant(), Email = email, NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true, DisplayName = $"QA User {offset}", PasswordHash = hash, SecurityStamp = Guid.NewGuid().ToString("N"), ConcurrencyStamp = Guid.NewGuid().ToString("N"), LockoutEnabled = true };
                user.Levels.Add(new UserLevel { UserId = user.Id, Level = "Externo" });
                database.Users.Add(user);
            }
            await database.SaveChangesAsync(token);
            database.ChangeTracker.Clear();
        }
    }
}
