using Acropolis.Identity.Application;

namespace Acropolis.Identity.UnitTests;

public sealed class OwnerRulesTests
{
    [Fact]
    public void AdministrativeAuthorityIsFiniteIndependentAndAlwaysRequiresMfa()
    {
        Assert.True(IdentityRules.ValidPermissions([]));
        foreach (var permission in IdentityRules.AdministrativePermissions)
        {
            Assert.True(IdentityRules.ValidPermissions([permission]));
            Assert.True(MfaRules.Required([permission]));
        }
        Assert.False(IdentityRules.ValidPermissions(null));
        Assert.False(IdentityRules.ValidPermissions(["Externo"]));
        Assert.False(IdentityRules.ValidPermissions([IdentityRules.ManageUsers, IdentityRules.ManageUsers]));
        Assert.False(IdentityRules.ValidPermissions(["a", "b", "c", "d"]));
        Assert.False(MfaRules.Required([]));
        Assert.True(IdentityRules.ValidVersion("version"));
        Assert.False(IdentityRules.ValidVersion(null));
        Assert.False(IdentityRules.ValidVersion(new string('v', 65)));
        Assert.False(IdentityRules.ValidVersion("bad\0version"));
    }
    [Fact]
    public void AuditSummariesNeverReturnStoredPayloads()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(IdentityRules.ValidAuditQuery(null, now, null, 1, 100));
        Assert.False(IdentityRules.ValidAuditQuery(now, now.AddDays(-1), null, 1, 20));
        Assert.False(IdentityRules.ValidAuditQuery(null, null, "unbounded", 1, 20));
        Assert.False(IdentityRules.ValidAuditQuery(null, null, null, 0, 20));
        Assert.False(IdentityRules.ValidAuditQuery(null, null, null, 1, 101));
        Assert.Contains("owner", IdentityRules.AuditSummary("owner.bootstrap").Fields);
        Assert.Contains("credentials", IdentityRules.AuditSummary("account.recovery_invalidated").Fields);
        Assert.Equal(["account"], IdentityRules.AuditSummary("users.updated").Fields);
        Assert.Equal(["account"], IdentityRules.AuditSummary("users.updated", "invalid").Fields);
        Assert.Equal(["mfa"], IdentityRules.AuditSummary("mfa.enabled").Fields);
        Assert.Equal(["displayName", "status", "levels"], IdentityRules.AuditSummary("users.updated", """{"displayNameChanged":true,"before":{"status":"active","levels":[]},"after":{"status":"disabled","levels":["Externo"]}}""").Fields);
        Assert.Empty(IdentityRules.AuditSummary("users.updated", "{}").Fields);
        Assert.Equal(["account"], IdentityRules.AuditSummary("users.updated", "[]").Fields);
        Assert.Empty(IdentityRules.AuditSummary("users.updated", """{"before":[],"after":[]}""").Fields);
        Assert.Equal(["permissions"], IdentityRules.AuditSummary("permissions.updated").Fields);
        var id = Guid.NewGuid(); Assert.Equal(IdentityRules.UserLock(id), IdentityRules.UserLock(id));
        Assert.NotEqual(IdentityRules.UserLock(id), IdentityRules.UserLock(Guid.NewGuid()));
    }
}
