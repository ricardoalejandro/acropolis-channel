using System.Security.Cryptography;
using System.Text;
using Acropolis.Identity.Application;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
namespace Acropolis.Identity.Infrastructure;

public sealed class MfaService(IdentityDbContext database, UserManager<ChannelUser> users, IDataProtectionProvider protection,
    MfaVerificationKey verification, TimeProvider clock) : IMfaService
{
    public async Task<IdentityResult<MfaChallengeView?>> BeginLoginAsync(AuthenticatedUser authentication, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var user = await LockUser(authentication.User.Id, token);
        if (!Active(user) || user!.SecurityVersion != authentication.SecurityVersion) return IdentityResult<MfaChallengeView?>.Fail("invalid_credentials", 401);
        if (!user.TwoFactorEnabled && !Required(user)) return new(null);
        var credential = await Credential(user, token);
        if (Locked(credential)) return IdentityResult<MfaChallengeView?>.Fail("rate_limited", 429);
        var (challenge, secret) = await NewChallenge(user, user.TwoFactorEnabled ? "login" : "enroll-init", null, token);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(new(true, !user.TwoFactorEnabled, secret, challenge.ExpiresUtc), Status: 202);
    }

    public async Task<IdentityResult<MfaStatusView>> StatusAsync(Guid userId, CancellationToken token)
    {
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, token);
        if (!Active(user)) return IdentityResult<MfaStatusView>.Fail("invalid_credentials", 401);
        return new(new(user!.TwoFactorEnabled, Required(user), await database.MfaRecoveryCodes.CountAsync(x => x.UserId == userId, token)));
    }

    public async Task<IdentityResult<MfaEnrollmentView>> StartEnrollmentAsync(Guid? authenticatedId, MfaEnrollmentRequest request, CancellationToken token)
    {
        if ((request.ChallengeToken is null) == (request.CurrentPassword is null)) return IdentityResult<MfaEnrollmentView>.Fail("validation_error");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        ChannelUser? user;
        if (request.ChallengeToken is not null)
        {
            var initial = await LockChallenge(request.ChallengeToken, "enroll-init", token);
            if (initial is null) return IdentityResult<MfaEnrollmentView>.Fail("challenge_invalid");
            user = await LockUser(initial.UserId, token);
            if (!Active(user) || initial.SecurityVersion != user!.SecurityVersion) return IdentityResult<MfaEnrollmentView>.Fail("challenge_invalid");
            initial.ConsumedUtc = clock.GetUtcNow();
        }
        else
        {
            if (authenticatedId is null) return IdentityResult<MfaEnrollmentView>.Fail("invalid_credentials", 401);
            user = await LockUser(authenticatedId.Value, token);
            if (!Active(user)) return IdentityResult<MfaEnrollmentView>.Fail("invalid_credentials", 401);
            var passwordCredential = await Credential(user!, token);
            if (Locked(passwordCredential)) return IdentityResult<MfaEnrollmentView>.Fail("rate_limited", 429);
            if (!IdentityRules.ValidPassword(request.CurrentPassword) || !await users.CheckPasswordAsync(user!, request.CurrentPassword!))
            {
                await Failed(null, passwordCredential, token);
                await transaction.CommitAsync(token);
                return IdentityResult<MfaEnrollmentView>.Fail("current_password_invalid", fields: new() { ["currentPassword"] = ["La contraseña actual no es correcta."] });
            }
        }
        if (user!.TwoFactorEnabled) return IdentityResult<MfaEnrollmentView>.Fail("mfa_already_enabled", 409);
        if (Locked(await Credential(user, token))) return IdentityResult<MfaEnrollmentView>.Fail("rate_limited", 429);
        var key = users.GenerateNewAuthenticatorKey();
        var (challenge, secret) = await NewChallenge(user, "enroll", KeyProtector(user.Id).Protect(key), token);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        var issuer = Uri.EscapeDataString("Acropolis Channel");
        var uri = $"otpauth://totp/{issuer}:{Uri.EscapeDataString(user.Email!)}?secret={key}&issuer={issuer}&digits=6&period=30";
        return new(new(secret, key, uri, challenge.ExpiresUtc));
    }

    public async Task<IdentityResult<MfaEnabledView>> EnableAsync(MfaEnableRequest request, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var challenge = await LockChallenge(request.ChallengeToken, "enroll", token);
        if (challenge is null) return IdentityResult<MfaEnabledView>.Fail("challenge_invalid");
        var user = await LockUser(challenge.UserId, token);
        if (!Active(user) || user!.TwoFactorEnabled || challenge.SecurityVersion != user.SecurityVersion) return IdentityResult<MfaEnabledView>.Fail("challenge_invalid");
        var credential = await Credential(user, token);
        if (Locked(credential)) return IdentityResult<MfaEnabledView>.Fail("rate_limited", 429);
        var key = KeyProtector(user.Id).Unprotect(challenge.ProtectedKey!);
        if (!await VerifyCode(user, request.Code, key, token))
        {
            await Failed(challenge, credential, token); await transaction.CommitAsync(token);
            return IdentityResult<MfaEnabledView>.Fail("mfa_invalid_code", fields: new() { ["code"] = ["Introduce un código válido y no utilizado."] });
        }
        credential.ProtectedKey = challenge.ProtectedKey!;
        user.TwoFactorEnabled = true;
        ResetFailures(credential);
        var codes = await ReplaceRecoveryCodes(user.Id, token);
        await Revoke(user, "mfa.enabled", token);
        await database.Entry(user).Collection(x => x.Levels).LoadAsync(token);
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(new(new(IdentityService.View(user), user.SecurityVersion), codes));
    }

    public async Task<IdentityResult<AuthenticatedUser>> VerifyAsync(MfaVerifyRequest request, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var challenge = await LockChallenge(request.ChallengeToken, "login", token);
        if (challenge is null) return IdentityResult<AuthenticatedUser>.Fail("challenge_invalid");
        var user = await LockUser(challenge.UserId, token);
        if (!Active(user) || !user!.TwoFactorEnabled || challenge.SecurityVersion != user.SecurityVersion) return IdentityResult<AuthenticatedUser>.Fail("challenge_invalid");
        var credential = await Credential(user, token);
        if (Locked(credential)) return IdentityResult<AuthenticatedUser>.Fail("rate_limited", 429);
        if (!await VerifyProof(user, request.Code, request.RecoveryCode, credential, token))
        {
            await Failed(challenge, credential, token); await transaction.CommitAsync(token);
            return IdentityResult<AuthenticatedUser>.Fail("mfa_invalid_code", fields: new() { [request.RecoveryCode is null ? "code" : "recoveryCode"] = ["Introduce un código válido y no utilizado."] });
        }
        challenge.ConsumedUtc = clock.GetUtcNow();
        ResetFailures(credential);
        await database.Entry(user).Collection(x => x.Levels).LoadAsync(token);
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(new(IdentityService.View(user), user.SecurityVersion));
    }

    public Task<IdentityResult<string[]>> RegenerateAsync(Guid userId, MfaReauthenticateRequest request, CancellationToken token) => ChangeSettings(userId, request, false, token);
    public async Task<IdentityResult<bool>> DisableAsync(Guid userId, MfaReauthenticateRequest request, CancellationToken token)
    {
        var result = await ChangeSettings(userId, request, true, token);
        return result.Succeeded ? new(true, Status: 204) : IdentityResult<bool>.Fail(result.Error!, result.Status, result.FieldErrors);
    }
    private async Task<IdentityResult<string[]>> ChangeSettings(Guid userId, MfaReauthenticateRequest request, bool disable, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var user = await LockUser(userId, token);
        if (!Active(user)) return IdentityResult<string[]>.Fail("invalid_credentials", 401);
        if (!user!.TwoFactorEnabled) return IdentityResult<string[]>.Fail("mfa_not_enabled", 409);
        if (disable && Required(user)) return IdentityResult<string[]>.Fail("mfa_required_for_admin", 409);
        var credential = await Credential(user, token);
        if (Locked(credential)) return IdentityResult<string[]>.Fail("rate_limited", 429);
        if (!IdentityRules.ValidPassword(request.CurrentPassword) || !await users.CheckPasswordAsync(user, request.CurrentPassword))
        {
            await Failed(null, credential, token); await transaction.CommitAsync(token);
            return IdentityResult<string[]>.Fail("current_password_invalid", fields: new() { ["currentPassword"] = ["La contraseña actual no es correcta."] });
        }
        if (!await VerifyProof(user, request.Code, request.RecoveryCode, credential, token))
        {
            await Failed(null, credential, token); await transaction.CommitAsync(token);
            return IdentityResult<string[]>.Fail("mfa_invalid_code");
        }
        string[] codes = [];
        if (disable)
        {
            user.TwoFactorEnabled = false;
            credential.ProtectedKey = "";
            await database.MfaRecoveryCodes.Where(x => x.UserId == userId).ExecuteDeleteAsync(token);
        }
        else codes = await ReplaceRecoveryCodes(userId, token);
        ResetFailures(credential);
        await Revoke(user, disable ? "mfa.disabled" : "mfa.codes_rotated", token);
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(codes);
    }

    private async Task<bool> VerifyProof(ChannelUser user, string? code, string? recovery, MfaCredential credential, CancellationToken token)
    {
        if (!MfaRules.ValidProof(code, recovery)) return false;
        if (recovery is not null)
            return await database.MfaRecoveryCodes.Where(x => x.UserId == user.Id && x.Hash == IdentityService.Hash(recovery)).ExecuteDeleteAsync(token) == 1;
        return !string.IsNullOrEmpty(credential.ProtectedKey) && await VerifyCode(user, code!, KeyProtector(user.Id).Unprotect(credential.ProtectedKey), token);
    }
    private async Task<bool> VerifyCode(ChannelUser user, string code, string key, CancellationToken token)
    {
        if (!MfaRules.ValidCode(code)) return false;
        verification.UserId = user.Id; verification.Key = key;
        bool valid;
        try { valid = await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code); }
        finally { verification.Key = null; }
        if (!valid) return false;
        // Identity validates the TOTP algorithm. This HMAC records only replay state.
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(code)));
        var now = clock.GetUtcNow();
        await database.MfaProofs.Where(x => x.UserId == user.Id && x.ExpiresUtc <= now).ExecuteDeleteAsync(token);
        if (await database.MfaProofs.AnyAsync(x => x.UserId == user.Id && x.Hash == hash, token)) return false;
        database.MfaProofs.Add(new MfaProof { UserId = user.Id, Hash = hash, ExpiresUtc = now + MfaRules.ReplayLifetime });
        return true;
    }
    private async Task<MfaChallenge?> LockChallenge(string secret, string purpose, CancellationToken token)
    {
        if (!MfaRules.ValidChallenge(secret)) return null;
        var hash = IdentityService.Hash(secret);
        var hint = await database.MfaChallenges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == hash, token);
        if (hint is null) return null;
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityService.UserLock(hint.UserId)})", token);
        var challenge = await database.MfaChallenges.SingleOrDefaultAsync(x => x.Id == hash, token);
        return challenge is not null && challenge.Purpose == purpose && challenge.ConsumedUtc is null && challenge.ExpiresUtc > clock.GetUtcNow() && challenge.Attempts < MfaRules.MaximumAttempts ? challenge : null;
    }
    private async Task<ChannelUser?> LockUser(Guid id, CancellationToken token)
    {
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityService.UserLock(id)})", token);
        var user = await database.Users.FromSqlInterpolated($"SELECT * FROM identity.\"Users\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync(token);
        if (user is not null) await database.Entry(user).ReloadAsync(token);
        return user;
    }
    private async Task<MfaCredential> Credential(ChannelUser user, CancellationToken token)
    {
        var result = database.MfaCredentials.Local.SingleOrDefault(x => x.UserId == user.Id)
            ?? await database.MfaCredentials.SingleOrDefaultAsync(x => x.UserId == user.Id, token);
        if (result is null) { result = new() { UserId = user.Id }; database.MfaCredentials.Add(result); }
        return result;
    }
    private bool Locked(MfaCredential credential) => credential.LockedUntilUtc > clock.GetUtcNow();
    private static bool Required(ChannelUser user) => user.UsersManage || user.ContentManage || user.SubscriptionsManage || user.IsOwner;
    private bool Active(ChannelUser? user) => user is not null && user.EmailConfirmed && !user.IsDisabled && !user.RevalidationRequired
        && (!user.LockoutEnabled || user.LockoutEnd is null || user.LockoutEnd <= clock.GetUtcNow());
    private IDataProtector KeyProtector(Guid userId) => protection.CreateProtector("Acropolis.Identity.MfaKey.v1", userId.ToString("N"));
    private static void ResetFailures(MfaCredential credential) { credential.FailedAttempts = 0; credential.LockedUntilUtc = null; }
    private async Task Failed(MfaChallenge? challenge, MfaCredential credential, CancellationToken token)
    {
        if (challenge is not null) challenge.Attempts++;
        credential.FailedAttempts++;
        if (credential.FailedAttempts >= MfaRules.MaximumAttempts) { credential.LockedUntilUtc = clock.GetUtcNow().AddMinutes(15); credential.FailedAttempts = 0; }
        await database.SaveChangesAsync(token);
    }
    private async Task<(MfaChallenge Challenge, string Secret)> NewChallenge(ChannelUser user, string purpose, string? key, CancellationToken token)
    {
        await database.MfaChallenges.Where(x => x.UserId == user.Id && x.ConsumedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedUtc, clock.GetUtcNow()).SetProperty(x => x.ProtectedKey, (string?)null), token);
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = new MfaChallenge { Id = IdentityService.Hash(secret), UserId = user.Id, SecurityVersion = user.SecurityVersion, Purpose = purpose, ProtectedKey = key, ExpiresUtc = clock.GetUtcNow() + MfaRules.ChallengeLifetime };
        database.MfaChallenges.Add(challenge);
        return (challenge, secret);
    }
    private async Task<string[]> ReplaceRecoveryCodes(Guid userId, CancellationToken token)
    {
        await database.MfaRecoveryCodes.Where(x => x.UserId == userId).ExecuteDeleteAsync(token);
        var codes = Enumerable.Range(0, 10).Select(_ => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16))).ToArray();
        database.MfaRecoveryCodes.AddRange(codes.Select(code => new MfaRecoveryCode { UserId = userId, Hash = IdentityService.Hash(code) }));
        return codes;
    }
    private async Task Revoke(ChannelUser user, string action, CancellationToken token)
    {
        user.SecurityVersion = Guid.NewGuid().ToString("N"); user.SecurityStamp = Guid.NewGuid().ToString("N"); user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();
        await database.Sessions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(token);
        await database.MfaChallenges.Where(x => x.UserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedUtc, now).SetProperty(x => x.ProtectedKey, (string?)null), token);
        await database.Flows.Where(x => x.UserId == user.Id && x.ConsumedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedUtc, now), token);
        await database.Outbox.Where(x => x.UserId == user.Id && (x.Status == "pending" || x.Status == "sending")).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "cancelled").SetProperty(x => x.Payload, ""), token);
        database.Audit.Add(new UserAudit { Id = Guid.NewGuid(), ActorId = user.Id, UserId = user.Id, Action = action, Changes = "MFA security state changed; credentials omitted.", CreatedUtc = now });
    }
}
