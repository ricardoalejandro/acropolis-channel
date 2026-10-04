namespace Acropolis.Identity.Infrastructure;

public sealed class IdentitySettings
{
    public string PublicOrigin { get; set; } = string.Empty;
    public string KnownProxies { get; set; } = string.Empty;
    public KeyProtectionSettings DataProtection { get; set; } = new();
    public SmtpSettings Smtp { get; set; } = new();
}
public sealed class KeyProtectionSettings
{
    public string KeyRingPath { get; set; } = string.Empty;
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificatePassword { get; set; } = string.Empty;
}
public sealed class SmtpSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "Acropolis Channel";
    public string Security { get; set; } = "starttls";
}
