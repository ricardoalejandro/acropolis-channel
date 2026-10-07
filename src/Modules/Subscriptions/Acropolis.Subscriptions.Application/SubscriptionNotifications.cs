using System.Globalization;

namespace Acropolis.Subscriptions.Application;

public sealed class SubscriptionNotificationOptions
{
    public bool Enabled { get; set; }
    public bool RunInTesting { get; set; }
}
public static class SubscriptionNotificationRules
{
    public const int BatchSize = 100;
    public const int MaximumAttempts = 5;
    public static readonly TimeSpan ReminderWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan SubmissionSpacing = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan LeaseLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SendDeadline = TimeSpan.FromSeconds(15);
    public static bool ReminderDue(string status, string plan, DateTimeOffset startsUtc, DateTimeOffset? expiresUtc, DateTimeOffset now) =>
        status == "active" && plan is SubscriptionRules.ProbationPlan or SubscriptionRules.AnnualPlan && startsUtc <= now &&
        expiresUtc is { } end && now < end && end <= now.Add(ReminderWindow);
    public static string EventKey(Guid auditId) => "audit:" + auditId.ToString("N", CultureInfo.InvariantCulture);
    public static string ReminderKey(Guid subscriptionId, Guid generation) =>
        "expiring:" + subscriptionId.ToString("N", CultureInfo.InvariantCulture) + ":" + generation.ToString("N", CultureInfo.InvariantCulture);
    public static TimeSpan RetryDelay(int attempts) => TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, Math.Clamp(attempts, 1, MaximumAttempts))));
}

public sealed record SubscriptionNotice(string Subject, string Text);
public static class SubscriptionNotificationMessages
{
    public static SubscriptionNotice Create(string kind, string plan, DateTimeOffset startsUtc, DateTimeOffset? expiresUtc, string publicOrigin)
    {
        if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new ArgumentException("Notification origin is invalid.", nameof(publicOrigin));
        var name = plan switch { SubscriptionRules.Plan => "Gratuito", SubscriptionRules.ProbationPlan => "Probacionismo", SubscriptionRules.AnnualPlan => "Anual", _ => throw new ArgumentException("Notification plan is invalid.", nameof(plan)) };
        var subject = kind switch { "assigned" => "Acrópolis Channel — Tu plan fue asignado", "renewed" => "Acrópolis Channel — Tu plan fue renovado", "expiring" => "Acrópolis Channel — Tu plan está próximo a vencer", _ => throw new ArgumentException("Notification kind is invalid.", nameof(kind)) };
        if (plan == SubscriptionRules.Plan && (expiresUtc is not null || kind != "assigned") || plan != SubscriptionRules.Plan && (expiresUtc is null || expiresUtc <= startsUtc))
            throw new ArgumentException("Notification terms are invalid.");
        var text = kind == "expiring" ? "Te avisamos que tu período está próximo a vencer." : kind == "renewed" ? "Un gestor ha renovado tu plan." : "Un gestor ha asignado tu plan.";
        text += "\n\nPlan: " + name + "\nInicio: " + Date(startsUtc);
        text += expiresUtc is { } end ? "\nVencimiento: " + Date(end) + "\nEl acceso a todo el catálogo corresponde al período indicado. La renovación es manual." : "\nSin fecha de vencimiento. El plan Gratuito permite acceder únicamente a las obras marcadas gratuitas.";
        text += "\n\nPuedes revisar tu suscripción en " + publicOrigin.TrimEnd('/') + "/profile/subscription\nEste es un aviso transaccional sobre tu cuenta; no es publicidad.";
        return new(subject, text);
    }
    private static string Date(DateTimeOffset value) => value.ToOffset(TimeSpan.FromHours(-5)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " (hora de Lima, UTC-05:00)";
}
