using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Mail;

namespace Acropolis.Identity.Application;

public static class IdentityRules
{
    public const long AdministrationLockKey = 719283401053L;
    public static long UserLock(Guid id) => BitConverter.ToInt64(SHA256.HashData(id.ToByteArray()), 0);
    public const string ManageUsers = "Users.Manage";
    public const string ManageContent = "Content.Manage";
    public const string ManageSubscriptions = "Subscriptions.Manage";
    public static IReadOnlyList<string> AdministrativePermissions { get; } = Array.AsReadOnly(new[] { ManageUsers, ManageContent, ManageSubscriptions });
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
    public static IReadOnlyList<string> AuditActions { get; } = Array.AsReadOnly(new[]
    {
        "users.updated", "permissions.updated", "admin.bootstrap", "owner.bootstrap", "owner.recovered", "account.revalidated",
        "account.recovery_invalidated", "content.permission.granted", "content.permission.revoked", "mfa.enabled", "mfa.disabled", "mfa.codes_rotated"
    });
    public static bool ValidAuditQuery(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? action, int page, int pageSize) =>
        page is >= 1 and <= 1000000 && pageSize is >= 1 and <= 100 && (fromUtc is null || toUtc is null || fromUtc <= toUtc)
        && (action is null || AuditActions.Contains(action));
    public static UserAuditChanges AuditSummary(string action, string? storedChanges = null)
    {
        if (action == "users.updated" && storedChanges is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(storedChanges);
                if (json.RootElement.ValueKind != JsonValueKind.Object) return new(["account"]);
                var fields = new List<string>();
                if (json.RootElement.TryGetProperty("displayNameChanged", out var name) && name.ValueKind == JsonValueKind.True) fields.Add("displayName");
                if (json.RootElement.TryGetProperty("before", out var before) && json.RootElement.TryGetProperty("after", out var after) && before.ValueKind == JsonValueKind.Object && after.ValueKind == JsonValueKind.Object)
                {
                    if (before.TryGetProperty("status", out var oldStatus) && after.TryGetProperty("status", out var newStatus) && oldStatus.GetRawText() != newStatus.GetRawText()) fields.Add("status");
                    if (before.TryGetProperty("levels", out var oldLevels) && after.TryGetProperty("levels", out var newLevels) && oldLevels.GetRawText() != newLevels.GetRawText()) fields.Add("levels");
                }
                return new(fields.ToArray());
            }
            catch (JsonException) { return new(["account"]); }
        }
        return new(action switch
        {
            "users.updated" => ["account"],
            "owner.bootstrap" or "owner.recovered" => ["owner", "permissions"],
            "account.revalidated" or "account.recovery_invalidated" => ["owner", "permissions", "credentials", "mfa"],
            "mfa.enabled" or "mfa.disabled" or "mfa.codes_rotated" => ["mfa"],
            _ => ["permissions"]
        });
    }
    public static bool ValidPermissions(string[]? permissions) => permissions is not null && permissions.Length <= AdministrativePermissions.Count
        && permissions.Distinct(StringComparer.Ordinal).Count() == permissions.Length && permissions.All(AdministrativePermissions.Contains);

    public static bool ValidVersion(string? version) => !string.IsNullOrWhiteSpace(version) && version.Length <= 64 && !version.Any(char.IsControl);

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
        if (!ValidVersion(request.Version)) fields["version"] = ["La versión es obligatoria."];
        if (request.DisplayName is not null && !ValidName(request.DisplayName)) fields["displayName"] = ["El nombre debe tener entre 2 y 100 caracteres."];
        if (request.Status is not null && request.Status is not ("active" or "disabled")) fields["status"] = ["Estado no válido."];
        if (!ValidLevels(request.Levels)) fields["levels"] = ["Niveles no válidos."];
        if (request.DisplayName is null && request.Status is null && request.Levels is null) fields["request"] = ["Incluye al menos un cambio."];
        return fields;
    }
}
