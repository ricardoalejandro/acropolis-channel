using System.Net.Mail;

namespace Acropolis.Identity.Application;

public static class IdentityRules
{
    public const string ManageUsers = "Users.Manage";
    public static IReadOnlyList<string> Levels { get; } = Array.AsReadOnly(new[] { "Externo", "Probacionista", "Miembro", "FFVV", "Instructor", "Hachado" });
    public static TimeSpan SessionLifetime => TimeSpan.FromHours(8);
    public static TimeSpan ConfirmationLifetime => TimeSpan.FromHours(24);
    public static TimeSpan ResetLifetime => TimeSpan.FromMinutes(30);

    public static bool ValidName(string? value) => value is not null && value.Trim().Length is >= 2 and <= 100 && !value.Any(char.IsControl);
    public static bool ValidPassword(string? value) => value is not null && value.Length is >= 15 and <= 128;
    public static bool ValidEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 254) return false;
        return MailAddress.TryCreate(value.Trim(), out var email) && email.Address == value.Trim() && email.Host.Contains('.');
    }
    public static bool ValidLevels(string[]? levels) => levels is null || (levels.Length <= Levels.Count && levels.Distinct(StringComparer.Ordinal).Count() == levels.Length && levels.All(Levels.Contains));
    public static string Status(bool confirmed, bool disabled, bool revalidationRequired = false) =>
        disabled || revalidationRequired ? "disabled" : confirmed ? "active" : "pending";
    public static bool CanDisable(bool targetHasManageUsers, int activeAdministrators) => !targetHasManageUsers || activeAdministrators > 1;

    public static Dictionary<string, string[]> ValidateRegister(RegisterRequest request)
    {
        var fields = new Dictionary<string, string[]>();
        if (!ValidName(request.DisplayName)) fields["displayName"] = ["El nombre debe tener entre 2 y 100 caracteres."];
        if (!ValidEmail(request.Email)) fields["email"] = ["Introduce un correo válido."];
        if (!ValidPassword(request.Password)) fields["password"] = ["La contraseña debe tener entre 15 y 128 caracteres."];
        return fields;
    }
    public static Dictionary<string, string[]> ValidateAdmin(AdminUserRequest request)
    {
        var fields = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Version)) fields["version"] = ["La versión es obligatoria."];
        if (request.DisplayName is not null && !ValidName(request.DisplayName)) fields["displayName"] = ["El nombre debe tener entre 2 y 100 caracteres."];
        if (request.Status is not null && request.Status is not ("active" or "disabled")) fields["status"] = ["Estado no válido."];
        if (!ValidLevels(request.Levels)) fields["levels"] = ["Niveles no válidos."];
        if (request.DisplayName is null && request.Status is null && request.Levels is null) fields["request"] = ["Incluye al menos un cambio."];
        return fields;
    }
}
