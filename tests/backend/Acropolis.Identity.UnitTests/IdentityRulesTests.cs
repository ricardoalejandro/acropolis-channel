using Acropolis.Identity.Application;

namespace Acropolis.Identity.UnitTests;

public sealed class IdentityRulesTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("x", false)]
    [InlineData("  Ana  ", true)]
    [InlineData("Ana\nMaria", false)]
    public void DisplayNameRequiresUsefulBoundedText(string? name, bool expected) => Assert.Equal(expected, IdentityRules.ValidName(name));
    [Fact]
    public void DisplayNameUpperBoundIsEnforced()
    {
        Assert.True(IdentityRules.ValidName(new string('a', 100)));
        Assert.False(IdentityRules.ValidName(new string('a', 101)));
    }
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("short password", false)]
    [InlineData("a long passphrase", true)]
    public void PasswordUsesLengthRatherThanArbitraryComposition(string? password, bool expected) => Assert.Equal(expected, IdentityRules.ValidPassword(password));
    [Fact]
    public void PasswordBoundaryAllowsSpacesAndUnicode()
    {
        Assert.True(IdentityRules.ValidPassword(new string(' ', 15)));
        Assert.True(IdentityRules.ValidPassword(new string('ñ', 128)));
        Assert.False(IdentityRules.ValidPassword(new string('a', 129)));
    }
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("invalid", false)]
    [InlineData("name <x@example.test>", false)]
    [InlineData(" x@example.test ", true)]
    [InlineData("x@localhost", false)]
    public void EmailMustBeAnAddressRatherThanAHeader(string? email, bool expected) => Assert.Equal(expected, IdentityRules.ValidEmail(email));
    [Fact]
    public void EmailLengthIsBounded() => Assert.False(IdentityRules.ValidEmail(new string('a', 250) + "@example.test"));
    [Fact]
    public void LevelsAreIndependentAndCannotGrantAdministrativePermissions()
    {
        Assert.True(IdentityRules.ValidLevels(null));
        Assert.True(IdentityRules.ValidLevels([]));
        Assert.True(IdentityRules.ValidLevels(["Externo", "Instructor", "Hachado"]));
        Assert.False(IdentityRules.ValidLevels(["Instructor", "Instructor"]));
        Assert.False(IdentityRules.ValidLevels(["Users.Manage"]));
        Assert.False(IdentityRules.ValidLevels(Enumerable.Repeat("Externo", 7).ToArray()));
        Assert.Equal(6, IdentityRules.Levels.Count);
    }
    [Theory]
    [InlineData(false, false, false, "pending")]
    [InlineData(true, false, false, "active")]
    [InlineData(true, true, false, "disabled")]
    [InlineData(true, false, true, "disabled")]
    public void AccountStateIncludesRecoveryQuarantine(bool confirmed, bool disabled, bool quarantine, string status) => Assert.Equal(status, IdentityRules.Status(confirmed, disabled, quarantine));
    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, true)]
    public void LastActiveAdministratorIsPreserved(bool administrator, int count, bool allowed) => Assert.Equal(allowed, IdentityRules.CanDisable(administrator, count));
    [Fact]
    public void RegistrationReportsEachInvalidField()
    {
        Assert.Equal(3, IdentityRules.ValidateRegister(new("", "invalid", "short")).Count);
        Assert.Empty(IdentityRules.ValidateRegister(new("Ana", "ana@example.test", "Acropolis secure phrase")));
    }
    [Fact]
    public void AdminUpdateRequiresVersionAndAnActualAllowedChange()
    {
        var invalid = IdentityRules.ValidateAdmin(new("", "", "pending", ["invalid"]));
        Assert.Equal(4, invalid.Count);
        Assert.Single(IdentityRules.ValidateAdmin(new("version")));
        Assert.Empty(IdentityRules.ValidateAdmin(new("version", "Ana", "active", ["Externo"])));
    }
    [Fact]
    public void LifetimesAreFixedAndDistinct()
    {
        Assert.Equal(TimeSpan.FromHours(8), IdentityRules.SessionLifetime);
        Assert.Equal(TimeSpan.FromHours(24), IdentityRules.ConfirmationLifetime);
        Assert.Equal(TimeSpan.FromMinutes(30), IdentityRules.ResetLifetime);
    }

    [Fact]
    public void ResultSeparatesSuccessFromPublicFailureAndRetainsValidationErrors()
    {
        var accepted = new IdentityResult<bool>(true, Status: 202);
        Assert.True(accepted.Succeeded);
        Assert.True(accepted.Value);
        Assert.Equal(202, accepted.Status);
        var fields = new Dictionary<string, string[]> { ["email"] = ["Introduce un correo válido."] };
        var rejected = IdentityResult<bool>.Fail("validation_error", 400, fields);
        Assert.False(rejected.Succeeded);
        Assert.False(rejected.Value);
        Assert.Same(fields, rejected.FieldErrors);
        Assert.Equal("validation_error", rejected.Error);
        Assert.Equal(400, rejected.Status);
    }
}
