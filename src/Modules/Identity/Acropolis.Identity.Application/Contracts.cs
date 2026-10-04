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
