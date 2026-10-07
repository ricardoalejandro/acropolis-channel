namespace Acropolis.Identity.Application;

public sealed record UserAccessView(DateTimeOffset? LastSignInUtc);

public interface IAccountAccessService
{
    Task<UserAccessView?> GetAsync(Guid userId, CancellationToken token);
}
