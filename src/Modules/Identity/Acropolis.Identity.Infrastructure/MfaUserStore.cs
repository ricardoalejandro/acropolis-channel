using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
namespace Acropolis.Identity.Infrastructure;

// The official provider obtains its key through IUserAuthenticatorKeyStore.
// An enrollment key exists only in this request scope until a code is verified.
public sealed class MfaVerificationKey
{
    public Guid UserId { get; set; }
    public string? Key { get; set; }
}
public sealed class MfaUserStore(IdentityDbContext context, IDataProtectionProvider protection, MfaVerificationKey verification)
    : UserOnlyStore<ChannelUser, IdentityDbContext, Guid>(context)
{
    public override async Task<string?> GetAuthenticatorKeyAsync(ChannelUser user, CancellationToken cancellationToken)
    {
        if (verification.UserId == user.Id && verification.Key is not null) return verification.Key;
        var key = await Context.MfaCredentials.Where(x => x.UserId == user.Id).Select(x => x.ProtectedKey).SingleOrDefaultAsync(cancellationToken);
        return string.IsNullOrEmpty(key) ? null : protection.CreateProtector("Acropolis.Identity.MfaKey.v1", user.Id.ToString("N")).Unprotect(key);
    }
    public override async Task SetAuthenticatorKeyAsync(ChannelUser user, string key, CancellationToken cancellationToken)
    {
        var credential = await Context.MfaCredentials.FindAsync([user.Id], cancellationToken);
        if (credential is null) { credential = new MfaCredential { UserId = user.Id }; Context.MfaCredentials.Add(credential); }
        credential.ProtectedKey = protection.CreateProtector("Acropolis.Identity.MfaKey.v1", user.Id.ToString("N")).Protect(key);
    }
}
