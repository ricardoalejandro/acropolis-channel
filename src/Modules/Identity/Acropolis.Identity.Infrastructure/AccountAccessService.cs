using Acropolis.Identity.Application;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Identity.Infrastructure;

public sealed class AccountAccessService(IdentityDbContext database) : IAccountAccessService
{
    public Task<UserAccessView?> GetAsync(Guid userId, CancellationToken token) =>
        (from user in database.Users.AsNoTracking()
         join access in database.AccountAccess.AsNoTracking() on user.Id equals access.UserId into recorded
         from access in recorded.DefaultIfEmpty()
         where user.Id == userId
         select new UserAccessView(access == null ? null : access.LastSignInUtc))
        .SingleOrDefaultAsync(token);
}
