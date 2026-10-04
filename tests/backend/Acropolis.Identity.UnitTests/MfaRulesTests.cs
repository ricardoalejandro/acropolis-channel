using Acropolis.Identity.Application;
namespace Acropolis.Identity.UnitTests;

public sealed class MfaRulesTests
{
    [Theory]
    [InlineData("000001", true)]
    [InlineData("123456", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData(" 12345", false)]
    [InlineData("１２３４５６", false)]
    [InlineData(null, false)]
    public void AuthenticatorCodesRequireSixAsciiDigits(string? value, bool expected) => Assert.Equal(expected, MfaRules.ValidCode(value));
    [Fact]
    public void ProofRequiresExactlyOneFactorAndBoundedUrlSafeSecrets()
    {
        Assert.True(MfaRules.ValidProof("000001", null)); Assert.True(MfaRules.ValidProof(null, new string('a', 22)));
        Assert.False(MfaRules.ValidProof("000001", new string('a', 22))); Assert.False(MfaRules.ValidProof(null, null));
        Assert.False(MfaRules.ValidProof(null, new string('a', 21))); Assert.False(MfaRules.ValidProof(null, new string('a', 21) + "!"));
        Assert.True(MfaRules.ValidChallenge(new string('a', 41) + "_-")); Assert.False(MfaRules.ValidChallenge(null));
        Assert.False(MfaRules.ValidChallenge(new string('a', 44))); Assert.False(MfaRules.ValidChallenge(new string('a', 42) + "!"));
        Assert.True(MfaRules.Required([IdentityRules.ManageUsers])); Assert.True(MfaRules.Required([IdentityRules.ManageContent]));
        Assert.False(MfaRules.Required(["Instructor", "Unknown.Manage"])); Assert.False(MfaRules.Required([]));
        Assert.Equal(TimeSpan.FromMinutes(5), MfaRules.ChallengeLifetime); Assert.Equal(TimeSpan.FromMinutes(3), MfaRules.ReplayLifetime);
    }
}
