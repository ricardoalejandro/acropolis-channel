namespace Acropolis.Identity.Application;

public static class MfaRules
{
    public static TimeSpan ChallengeLifetime => TimeSpan.FromMinutes(5);
    public static TimeSpan ReplayLifetime => TimeSpan.FromMinutes(3);
    public const int MaximumAttempts = 5;
    public static bool ValidCode(string? value) => value is { Length: 6 } && value.All(c => c is >= '0' and <= '9');
    public static bool ValidChallenge(string? value) => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static bool ValidProof(string? code, string? recovery) => (ValidCode(code) && recovery is null) || (code is null && recovery is { Length: 22 } && recovery.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    public static bool Required(IEnumerable<string> permissions) => permissions.Any(p => p is IdentityRules.ManageUsers or IdentityRules.ManageContent or IdentityRules.ManageSubscriptions);
}
