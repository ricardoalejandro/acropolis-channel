using Acropolis.Identity.Application;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Identity.Infrastructure;

public sealed class AccountNotificationRecipient(IdentityDbContext database) : IAccountNotificationRecipient
{
    public Task<NotificationRecipient?> GetAsync(Guid userId, CancellationToken token) => database.Users.AsNoTracking()
        .Where(x => x.Id == userId && x.EmailConfirmed && !x.IsDisabled && !x.RevalidationRequired && x.Email != null)
        .Select(x => new NotificationRecipient(x.Email!)).SingleOrDefaultAsync(token);
}
