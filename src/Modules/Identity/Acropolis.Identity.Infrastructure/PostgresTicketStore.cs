using System.Security.Claims;
using System.Security.Cryptography;
using Acropolis.Identity.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Identity.Infrastructure;

public sealed class PostgresTicketStore(IDbContextFactory<IdentityDbContext> factory, IDataProtectionProvider protection, TimeProvider clock) : ITicketStore
{
    private readonly IDataProtector protector = protection.CreateProtector("Acropolis.Identity.Tickets.v1");
    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        var userId = Guid.Parse(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var version = ticket.Principal.FindFirstValue("auth_version")!;
        var expiry = ticket.Properties.ExpiresUtc ?? now + IdentityRules.SessionLifetime;
        if (expiry > now + IdentityRules.SessionLifetime) expiry = now + IdentityRules.SessionLifetime;
        ticket.Properties.ExpiresUtc = expiry;
        await using var database = await factory.CreateDbContextAsync();
        database.Sessions.Add(new StoredSession { Id = IdentityService.Hash(key), UserId = userId, SecurityVersion = version, Ticket = protector.Protect(TicketSerializer.Default.Serialize(ticket)), CreatedUtc = now, ExpiresUtc = expiry });
        await database.SaveChangesAsync();
        return key;
    }
    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        await using var database = await factory.CreateDbContextAsync();
        var session = await database.Sessions.FindAsync(IdentityService.Hash(key));
        if (session is null || session.ExpiresUtc <= clock.GetUtcNow()) return;
        ticket.Properties.ExpiresUtc = session.ExpiresUtc;
        session.Ticket = protector.Protect(TicketSerializer.Default.Serialize(ticket));
        await database.SaveChangesAsync();
    }
    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 256) return null;
        await using var database = await factory.CreateDbContextAsync();
        var id = IdentityService.Hash(key);
        var now = clock.GetUtcNow();
        var bytes = await (from session in database.Sessions.AsNoTracking()
                           join user in database.Users.AsNoTracking() on session.UserId equals user.Id
                           where session.Id == id && session.ExpiresUtc > now && user.EmailConfirmed
                               && !user.IsDisabled && !user.RevalidationRequired && user.SecurityVersion == session.SecurityVersion
                           select session.Ticket).SingleOrDefaultAsync();
        if (bytes is null) return null;
        try { return TicketSerializer.Default.Deserialize(protector.Unprotect(bytes)); }
        catch (CryptographicException) { return null; }
    }
    public async Task RemoveAsync(string key)
    {
        await using var database = await factory.CreateDbContextAsync();
        await database.Sessions.Where(x => x.Id == IdentityService.Hash(key)).ExecuteDeleteAsync();
    }
}
