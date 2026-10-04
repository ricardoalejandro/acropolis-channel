namespace Acropolis.Identity.Application;

public sealed record RegisterRequest(string DisplayName, string Email, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record EmailRequest(string Email);
public sealed record ConfirmEmailRequest(Guid UserId, string Token);
public sealed record ResetPasswordRequest(Guid UserId, string Token, string NewPassword);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ProfileRequest(string DisplayName);
public sealed record AdminUserRequest(string Version, string? DisplayName = null, string? Status = null, string[]? Levels = null);
public sealed record UserView(Guid Id, string DisplayName, string Email, bool EmailConfirmed, string Status, string[] Levels, string[] Permissions, string Version);
public sealed record UserPage(UserView[] Items, int Total, int Page, int PageSize);
public sealed record AuthenticatedUser(UserView User, string SecurityVersion);
public interface IIdentityService
{
    Task<IdentityResult<bool>> RegisterAsync(RegisterRequest request, CancellationToken token);
    Task<IdentityResult<AuthenticatedUser>> LoginAsync(LoginRequest request, CancellationToken token);
    Task RequestEmailAsync(string email, string purpose, CancellationToken token);
    Task<IdentityResult<bool>> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken token);
    Task<IdentityResult<bool>> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken token);
    Task<IdentityResult<bool>> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken token);
    Task<UserView?> GetUserAsync(Guid userId, CancellationToken token);
    Task<IdentityResult<UserView>> UpdateProfileAsync(Guid userId, ProfileRequest request, CancellationToken token);
    Task<UserPage> ListUsersAsync(string? search, string? status, string? level, int page, int pageSize, CancellationToken token);
    Task<IdentityResult<UserView>> UpdateUserAsync(Guid actorId, Guid userId, AdminUserRequest request, CancellationToken token);
}

public sealed record MfaChallengeView(bool MfaRequired, bool EnrollmentRequired, string ChallengeToken, DateTimeOffset ExpiresUtc);
public sealed record MfaEnrollmentRequest(string? ChallengeToken = null, string? CurrentPassword = null);
public sealed record MfaEnrollmentView(string ChallengeToken, string SharedKey, string AuthenticatorUri, DateTimeOffset ExpiresUtc);
public sealed record MfaEnableRequest(string ChallengeToken, string Code);
public sealed record MfaVerifyRequest(string ChallengeToken, string? Code = null, string? RecoveryCode = null);
public sealed record MfaReauthenticateRequest(string CurrentPassword, string? Code = null, string? RecoveryCode = null);
public sealed record MfaStatusView(bool Enabled, bool Required, int RecoveryCodesLeft);
public sealed record MfaEnabledView(AuthenticatedUser Authentication, string[] RecoveryCodes);
public interface IMfaService
{
    Task<IdentityResult<MfaChallengeView?>> BeginLoginAsync(AuthenticatedUser authentication, CancellationToken token);
    Task<IdentityResult<MfaEnrollmentView>> StartEnrollmentAsync(Guid? authenticatedId, MfaEnrollmentRequest request, CancellationToken token);
    Task<IdentityResult<MfaEnabledView>> EnableAsync(MfaEnableRequest request, CancellationToken token);
    Task<IdentityResult<AuthenticatedUser>> VerifyAsync(MfaVerifyRequest request, CancellationToken token);
    Task<IdentityResult<MfaStatusView>> StatusAsync(Guid userId, CancellationToken token);
    Task<IdentityResult<string[]>> RegenerateAsync(Guid userId, MfaReauthenticateRequest request, CancellationToken token);
    Task<IdentityResult<bool>> DisableAsync(Guid userId, MfaReauthenticateRequest request, CancellationToken token);
}
