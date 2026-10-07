using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Acropolis.Identity.Application;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acropolis.Identity.Infrastructure;

public sealed class IdentityService(IdentityDbContext database, UserManager<ChannelUser> users, IDataProtectionProvider protection, TimeProvider clock) : IIdentityService
{
    public const long AdministrationLockKey = IdentityRules.AdministrationLockKey;
    private readonly IDataProtector outboxProtector = protection.CreateProtector("Acropolis.Identity.Outbox.v1");

    public async Task<IdentityResult<bool>> RegisterAsync(RegisterRequest request, CancellationToken token)
    {
        var fields = IdentityRules.ValidateRegister(request);
        if (fields.Count > 0) return IdentityResult<bool>.Fail("validation_error", fields: fields);
        var email = request.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            // Perform the same expensive password work without revealing the existing account.
            _ = users.PasswordHasher.HashPassword(new ChannelUser(), request.Password);
            return new(true, Status: 202);
        }
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var user = new ChannelUser { Id = Guid.NewGuid(), UserName = email, Email = email, DisplayName = request.DisplayName.Trim(), LockoutEnabled = true };
        IdentityResult result;
        try { result = await users.CreateAsync(user, request.Password); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new(true, Status: 202);
        }
        if (!result.Succeeded)
        {
            if (result.Errors.Any(x => x.Code.StartsWith("Duplicate", StringComparison.Ordinal))) return new(true, Status: 202);
            return IdentityResult<bool>.Fail("validation_error");
        }
        user.Levels.Add(new UserLevel { UserId = user.Id, Level = "Externo" });
        await QueueFlowAsync(user, "confirm", token);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(true, Status: 202);
    }

    public async Task<IdentityResult<AuthenticatedUser>> LoginAsync(LoginRequest request, CancellationToken token)
    {
        if (!IdentityRules.ValidEmail(request.Email) || !IdentityRules.ValidPassword(request.Password)) return IdentityResult<AuthenticatedUser>.Fail("invalid_credentials", 401);
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            _ = users.PasswordHasher.HashPassword(new ChannelUser(), request.Password);
            return IdentityResult<AuthenticatedUser>.Fail("invalid_credentials", 401);
        }
        if (user.IsDisabled || user.RevalidationRequired || !user.EmailConfirmed || await users.IsLockedOutAsync(user))
        {
            _ = users.PasswordHasher.HashPassword(new ChannelUser(), request.Password);
            return IdentityResult<AuthenticatedUser>.Fail("invalid_credentials", 401);
        }
        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return IdentityResult<AuthenticatedUser>.Fail("invalid_credentials", 401);
        }
        await users.ResetAccessFailedCountAsync(user);
        await database.Entry(user).Collection(x => x.Levels).LoadAsync(token);
        return new(new AuthenticatedUser(View(user), user.SecurityVersion));
    }

    public async Task RequestEmailAsync(string email, string purpose, CancellationToken token)
    {
        if (!IdentityRules.ValidEmail(email) || purpose is not ("confirm" or "reset")) return;
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || user.IsDisabled || user.RevalidationRequired || (purpose == "confirm" && user.EmailConfirmed) || (purpose == "reset" && !user.EmailConfirmed)) return;
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({UserLock(user.Id)})", token);
        var now = clock.GetUtcNow();
        if (await database.Flows.AnyAsync(x => x.UserId == user.Id && x.Purpose == purpose && x.CreatedUtc > now.AddMinutes(-1), token)) return;
        await QueueFlowAsync(user, purpose, token);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<IdentityResult<bool>> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var flow = await LockFlowAsync(request.UserId, request.Token, "confirm", token);
        if (flow is null) return IdentityResult<bool>.Fail("invalid_token");
        var user = await users.FindByIdAsync(request.UserId.ToString());
        if (user is null || user.IsDisabled || user.RevalidationRequired) return IdentityResult<bool>.Fail("invalid_token");
        user.EmailConfirmed = true;
        flow.ConsumedUtc = clock.GetUtcNow();
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(true, Status: 204);
    }

    public async Task<IdentityResult<bool>> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken token)
    {
        if (!IdentityRules.ValidPassword(request.NewPassword)) return IdentityResult<bool>.Fail("validation_error", fields: new() { ["newPassword"] = ["La contraseña debe tener entre 15 y 128 caracteres."] });
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var flow = await LockFlowAsync(request.UserId, request.Token, "reset", token);
        if (flow is null) return IdentityResult<bool>.Fail("invalid_token");
        var user = await users.FindByIdAsync(request.UserId.ToString());
        if (user is null || user.IsDisabled || user.RevalidationRequired || !user.EmailConfirmed) return IdentityResult<bool>.Fail("invalid_token");
        user.PasswordHash = users.PasswordHasher.HashPassword(user, request.NewPassword);
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await InvalidateUserAsync(user, token);
        flow.ConsumedUtc = clock.GetUtcNow();
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(true, Status: 204);
    }

    public async Task<IdentityResult<bool>> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken token)
    {
        if (!IdentityRules.ValidPassword(request.NewPassword) || !IdentityRules.ValidPassword(request.CurrentPassword)) return IdentityResult<bool>.Fail("validation_error");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({UserLock(userId)})", token);
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDisabled || user.RevalidationRequired || !user.EmailConfirmed) return IdentityResult<bool>.Fail("invalid_credentials", 401);
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(error => error.Code == "PasswordMismatch")) return InvalidCurrentPassword();
            if (result.Errors.Any(error => error.Code == "ConcurrencyFailure")) return IdentityResult<bool>.Fail("concurrency_conflict", 409);
            return IdentityResult<bool>.Fail("validation_error");
        }
        await InvalidateUserAsync(user, token);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(true, Status: 204);
    }

    public async Task<UserView?> GetUserAsync(Guid userId, CancellationToken token)
    {
        var user = await database.Users.AsNoTracking().Include(x => x.Levels).SingleOrDefaultAsync(x => x.Id == userId, token);
        return user is null ? null : View(user);
    }

    public async Task<IdentityAccountSummary[]> LookupAccountsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken token)
    {
        if (userIds.Count is < 1 or > 20 || userIds.Contains(Guid.Empty) || userIds.Distinct().Count() != userIds.Count)
            throw new ArgumentException("Account lookup requires between one and twenty unique nonempty identifiers.", nameof(userIds));
        var ids = userIds.ToArray();
        return await database.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id)
            .Select(x => new IdentityAccountSummary(x.Id, x.DisplayName, x.Email!,
                x.IsDisabled || x.RevalidationRequired ? "disabled" : x.EmailConfirmed ? "active" : "pending",
                x.EmailConfirmed)).ToArrayAsync(token);
    }

    public async Task<IdentityResult<UserView>> UpdateProfileAsync(Guid userId, ProfileRequest request, CancellationToken token)
    {
        if (!IdentityRules.ValidName(request.DisplayName)) return IdentityResult<UserView>.Fail("validation_error", fields: new() { ["displayName"] = ["El nombre debe tener entre 2 y 100 caracteres."] });
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({UserLock(userId)})", token);
        var user = await database.Users.Include(x => x.Levels).SingleOrDefaultAsync(x => x.Id == userId, token);
        if (user is null || !user.EmailConfirmed || user.IsDisabled || user.RevalidationRequired) return IdentityResult<UserView>.Fail("invalid_credentials", 401);
        if (user.DisplayName == request.DisplayName.Trim()) return new(View(user));
        user.DisplayName = request.DisplayName.Trim();
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(View(user));
    }

    public async Task<UserPage> ListUsersAsync(string? search, string? status, string? level, int page, int pageSize, CancellationToken token)
    {
        var query = database.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace(@"\", @"\\", StringComparison.Ordinal)
                .Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
            query = query.Where(x => EF.Functions.ILike(x.NormalizedEmail!, pattern, @"\")
                || EF.Functions.ILike(x.DisplayName, pattern, @"\"));
        }
        if (status == "active") query = query.Where(x => x.EmailConfirmed && !x.IsDisabled && !x.RevalidationRequired);
        if (status == "pending") query = query.Where(x => !x.EmailConfirmed && !x.IsDisabled && !x.RevalidationRequired);
        if (status == "disabled") query = query.Where(x => x.IsDisabled || x.RevalidationRequired);
        if (level is not null) query = query.Where(x => x.Levels.Any(item => item.Level == level));
        var total = await query.CountAsync(token);
        var found = await query.Include(x => x.Levels).OrderBy(x => x.NormalizedEmail).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(token);
        return new(found.Select(View).ToArray(), total, page, pageSize);
    }

    public async Task<IdentityResult<UserView>> UpdateUserAsync(Guid actorId, Guid userId, AdminUserRequest request, CancellationToken token)
    {
        var fields = IdentityRules.ValidateAdmin(request);
        if (fields.Count > 0) return IdentityResult<UserView>.Fail("validation_error", fields: fields);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdministrationLockKey})", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({UserLock(userId)})", token);
        var actor = await database.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actorId, token);
        if (actor is null || !actor.UsersManage || !Active(actor)) return IdentityResult<UserView>.Fail("forbidden", 403);
        var user = await database.Users.Include(x => x.Levels).SingleOrDefaultAsync(x => x.Id == userId, token);
        if (user is null) return IdentityResult<UserView>.Fail("not_found", 404);
        if (user.ConcurrencyStamp != request.Version) return IdentityResult<UserView>.Fail("concurrency_conflict", 409);
        if (user.IsOwner && (actorId != userId || request.Status == "disabled")) return IdentityResult<UserView>.Fail("owner_protected", 409);
        if (request.Status == "disabled" && Active(user) && user.UsersManage)
        {
            var administrators = await database.Users.CountAsync(x => x.UsersManage && !x.IsDisabled && !x.RevalidationRequired && x.EmailConfirmed, token);
            if (!IdentityRules.CanDisable(true, administrators)) return IdentityResult<UserView>.Fail("last_admin", 409);
        }
        if (request.Status == "active" && user.RevalidationRequired) return IdentityResult<UserView>.Fail("account_unconfirmed", 409);
        var displayNameChanged = request.DisplayName is not null && user.DisplayName != request.DisplayName.Trim();
        var statusChanged = request.Status is not null && user.IsDisabled != (request.Status == "disabled");
        var levelsChanged = request.Levels is not null && !user.Levels.Select(x => x.Level).ToHashSet(StringComparer.Ordinal).SetEquals(request.Levels);
        if (!displayNameChanged && !statusChanged && !levelsChanged) return new(View(user));
        var before = new { status = IdentityRules.Status(user.EmailConfirmed, user.IsDisabled, user.RevalidationRequired), levels = user.Levels.Select(x => x.Level).Order().ToArray() };
        if (displayNameChanged) user.DisplayName = request.DisplayName!.Trim();
        if (statusChanged) user.IsDisabled = request.Status == "disabled";
        if (levelsChanged)
        {
            database.RemoveRange(user.Levels.Where(x => !request.Levels!.Contains(x.Level)).ToArray());
            user.Levels.RemoveAll(x => !request.Levels!.Contains(x.Level));
            foreach (var level in request.Levels!.Except(user.Levels.Select(x => x.Level))) user.Levels.Add(new UserLevel { UserId = user.Id, Level = level });
        }
        if (statusChanged || levelsChanged) await InvalidateUserAsync(user, token);
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        var after = new { status = IdentityRules.Status(user.EmailConfirmed, user.IsDisabled, user.RevalidationRequired), levels = user.Levels.Select(x => x.Level).Order().ToArray() };
        database.Audit.Add(new UserAudit { Id = Guid.NewGuid(), ActorId = actorId, UserId = userId, Action = "users.updated", Changes = JsonSerializer.Serialize(new { before, after, displayNameChanged }), CreatedUtc = clock.GetUtcNow() });
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(View(user));
    }

    public async Task<UserAuditPage> ListAuditAsync(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? action, Guid? userId, int page, int pageSize, CancellationToken token)
    {
        var query = database.Audit.AsNoTracking();
        if (fromUtc is not null) query = query.Where(x => x.CreatedUtc >= fromUtc.Value.ToUniversalTime());
        if (toUtc is not null) query = query.Where(x => x.CreatedUtc <= toUtc.Value.ToUniversalTime());
        if (action is not null) query = query.Where(x => x.Action == action);
        if (userId is not null) query = query.Where(x => x.UserId == userId.Value);
        var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(x => x.CreatedUtc).ThenByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.UserId, x.ActorId, x.Action, x.Changes, x.CreatedUtc }).ToArrayAsync(token);
        return new UserAuditPage(rows.Select(x => new UserAuditView(x.Id, x.UserId, x.ActorId, x.Action, IdentityRules.AuditSummary(x.Action, x.Changes), x.CreatedUtc)).ToArray(), total, page, pageSize);
    }

    public async Task<IdentityResult<UserView>> UpdatePermissionsAsync(Guid actorId, Guid userId, AdminPermissionsRequest request, CancellationToken token)
    {
        if (!IdentityRules.ValidVersion(request.Version) || !IdentityRules.ValidPermissions(request.Permissions))
            return IdentityResult<UserView>.Fail("validation_error");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdministrationLockKey})", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({UserLock(userId)})", token);
        var actor = await database.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actorId, token);
        if (actor is null || !actor.IsOwner || !Active(actor)) return IdentityResult<UserView>.Fail("forbidden", 403);
        var user = await database.Users.Include(x => x.Levels).SingleOrDefaultAsync(x => x.Id == userId, token);
        if (user is null) return IdentityResult<UserView>.Fail("not_found", 404);
        if (user.ConcurrencyStamp != request.Version) return IdentityResult<UserView>.Fail("concurrency_conflict", 409);
        if (user.IsOwner && !request.Permissions.ToHashSet(StringComparer.Ordinal).SetEquals(IdentityRules.AdministrativePermissions))
            return IdentityResult<UserView>.Fail("owner_protected", 409);
        if (request.Permissions.Length > 0 && !Active(user)) return IdentityResult<UserView>.Fail("account_not_active", 403);
        var before = Permissions(user);
        if (before.ToHashSet(StringComparer.Ordinal).SetEquals(request.Permissions)) return new(View(user));
        if (user.UsersManage && Active(user) && !request.Permissions.Contains(IdentityRules.ManageUsers))
        {
            var administrators = await database.Users.CountAsync(x => x.UsersManage && !x.IsDisabled && !x.RevalidationRequired && x.EmailConfirmed, token);
            if (!IdentityRules.CanDisable(true, administrators)) return IdentityResult<UserView>.Fail("last_admin", 409);
        }
        user.UsersManage = request.Permissions.Contains(IdentityRules.ManageUsers);
        user.ContentManage = request.Permissions.Contains(IdentityRules.ManageContent);
        user.SubscriptionsManage = request.Permissions.Contains(IdentityRules.ManageSubscriptions);
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await InvalidateUserAsync(user, token);
        database.Audit.Add(new UserAudit
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            UserId = userId,
            Action = "permissions.updated",
            Changes = JsonSerializer.Serialize(new { before, after = Permissions(user) }),
            CreatedUtc = clock.GetUtcNow()
        });
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(View(user));
    }

    private static bool Active(ChannelUser user) => user.EmailConfirmed && !user.IsDisabled && !user.RevalidationRequired;

    private static IdentityResult<bool> InvalidCurrentPassword() => IdentityResult<bool>.Fail("current_password_invalid", 400,
        fields: new() { ["currentPassword"] = ["La contraseña actual no es correcta."] });

    private async Task QueueFlowAsync(ChannelUser user, string purpose, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        await database.Flows.Where(x => x.UserId == user.Id && x.Purpose == purpose && x.ConsumedUtc == null).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ConsumedUtc, now), token);
        await database.Outbox.Where(x => x.UserId == user.Id && (x.Status == "pending" || x.Status == "sending")).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "cancelled").SetProperty(x => x.Payload, ""), token);
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var flow = new IdentityFlow { Id = Guid.NewGuid(), UserId = user.Id, Purpose = purpose, TokenHash = Hash(secret), CreatedUtc = now, ExpiresUtc = now + (purpose == "confirm" ? IdentityRules.ConfirmationLifetime : IdentityRules.ResetLifetime) };
        database.Flows.Add(flow);
        database.Outbox.Add(new OutboxMessage { Id = Guid.NewGuid(), UserId = user.Id, FlowId = flow.Id, Payload = outboxProtector.Protect(JsonSerializer.Serialize(new MailPayload(user.Email!, user.Id, secret, purpose, flow.Id))), NextAttemptUtc = now });
    }
    private async Task<IdentityFlow?> LockFlowAsync(Guid userId, string? secret, string purpose, CancellationToken token)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 256) return null;
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({UserLock(userId)})", token);
        var hash = Hash(secret);
        var flow = await database.Flows.FromSqlInterpolated($"SELECT * FROM identity.\"Flows\" WHERE \"UserId\"={userId} AND \"TokenHash\"={hash} AND \"Purpose\"={purpose} FOR UPDATE").SingleOrDefaultAsync(token);
        return flow is not null && flow.ConsumedUtc is null && flow.ExpiresUtc > clock.GetUtcNow() ? flow : null;
    }
    private async Task InvalidateUserAsync(ChannelUser user, CancellationToken token)
    {
        user.SecurityVersion = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();
        await database.Sessions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(token);
        await database.Flows.Where(x => x.UserId == user.Id && x.ConsumedUtc == null).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ConsumedUtc, now), token);
        await database.Outbox.Where(x => x.UserId == user.Id && (x.Status == "pending" || x.Status == "sending")).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "cancelled").SetProperty(x => x.Payload, ""), token);
    }
    internal static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public static long UserLock(Guid id) => IdentityRules.UserLock(id);
    internal static UserView View(ChannelUser user) => new(user.Id, user.DisplayName, user.Email!, user.EmailConfirmed,
        IdentityRules.Status(user.EmailConfirmed, user.IsDisabled, user.RevalidationRequired),
        user.Levels.Select(x => x.Level).Order(StringComparer.Ordinal).ToArray(),
        Permissions(user), user.ConcurrencyStamp!, user.IsOwner);
    private static string[] Permissions(ChannelUser user)
    {
        var permissions = new List<string>();
        if (user.UsersManage) permissions.Add(IdentityRules.ManageUsers);
        if (user.ContentManage) permissions.Add(IdentityRules.ManageContent);
        if (user.SubscriptionsManage) permissions.Add(IdentityRules.ManageSubscriptions);
        return permissions.ToArray();
    }
}
public sealed record MailPayload(string Email, Guid UserId, string Token, string Purpose, Guid MessageId);
