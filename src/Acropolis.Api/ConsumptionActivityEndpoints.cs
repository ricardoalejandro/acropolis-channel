using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Acropolis.Catalog.Application;
using Acropolis.Identity.Application;
using Acropolis.Subscriptions.Application;

public static class ConsumptionActivityEndpoints
{
    public static void MapChannelConsumptionActivity(this WebApplication app)
    {
        var consumption = app.MapGroup("/api/v1/consumption").RequireAuthorization();
        consumption.MapGet("/capabilities", (HttpContext context, IConsumptionRecordingService service) =>
            context.Request.QueryString.HasValue ? IdentityEndpoints.Problem("validation_error", 400) : Results.Ok(service.GetCapabilities()));
        var writes = consumption.MapGroup("").AddEndpointFilter<CsrfFilter>().RequireRateLimiting("consumption-write");
        writes.MapPost("/content/{slug}/sessions", async (string slug, StartConsumptionRequest request, HttpContext context, IIdentityService identity, ISubscriptionAccess access, IConsumptionRecordingService service, CancellationToken token) =>
        {
            var eligibility = await EligibleAsync(context, identity, access, token);
            if (eligibility.Error is { } error) return error;
            if (context.Request.QueryString.HasValue) return IdentityEndpoints.Problem("validation_error", 400);
            return Result(await service.StartAsync(eligibility.AccountId, eligibility.Binding!, slug, request, eligibility.AllowRestricted, token));
        });
        writes.MapPost("/sessions/{sessionId:guid}/pulses", async (Guid sessionId, ConsumptionPulseRequest request, HttpContext context, IIdentityService identity, ISubscriptionAccess access, IConsumptionRecordingService service, CancellationToken token) =>
        {
            var eligibility = await EligibleAsync(context, identity, access, token);
            if (eligibility.Error is { } error) return error;
            if (context.Request.QueryString.HasValue) return IdentityEndpoints.Problem("validation_error", 400);
            return Result(await service.PulseAsync(eligibility.AccountId, eligibility.Binding!, sessionId, request, eligibility.AllowRestricted, token));
        });
        app.MapGet("/api/v1/admin/reports/catalog/consumption", async (HttpContext context, IConsumptionActivityReportService service, CancellationToken token) =>
        {
            if (!TryQuery(context, false, out var interval, out _, out _)) return IdentityEndpoints.Problem("validation_error", 400);
            return Results.Ok(await service.GetAsync(interval!, token));
        }).RequireAuthorization(IdentityRules.ManageContent);
        app.MapGet("/api/v1/admin/reports/catalog/consumption/content", async (HttpContext context, IConsumptionActivityReportService service, CancellationToken token) =>
        {
            if (!TryQuery(context, true, out var interval, out var page, out var pageSize)) return IdentityEndpoints.Problem("validation_error", 400);
            return Results.Ok(await service.ListContentAsync(interval!, page, pageSize, token));
        }).RequireAuthorization(IdentityRules.ManageContent);
        app.MapGet("/api/v1/admin/users/{id:guid}/consumption", async (Guid id, HttpContext context, IIdentityService identity, IConsumptionActivityReportService service, CancellationToken token) =>
        {
            if (!TryQuery(context, true, out var interval, out var page, out var pageSize)) return IdentityEndpoints.Problem("validation_error", 400);
            if (await identity.GetUserAsync(id, token) is null) return IdentityEndpoints.Problem("not_found", 404);
            return Results.Ok(await service.GetAccountAsync(id, interval!, page, pageSize, token));
        }).RequireAuthorization(IdentityRules.ManageUsers, IdentityRules.ManageContent);
    }
    private sealed record Eligibility(Guid AccountId, string? Binding = null, bool AllowRestricted = false, IResult? Error = null);
    private static async Task<Eligibility> EligibleAsync(HttpContext context, IIdentityService identity, ISubscriptionAccess access, CancellationToken token)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) || id == Guid.Empty)
            return new(Guid.Empty, Error: IdentityEndpoints.Problem("invalid_credentials", 401));
        var user = await identity.GetUserAsync(id, token);
        var securityVersion = context.User.FindFirstValue("auth_version");
        if (user is null || !user.EmailConfirmed || user.Status != "active" || !ConsumptionActivityRules.ValidVersion(securityVersion))
            return new(Guid.Empty, Error: IdentityEndpoints.Problem("invalid_credentials", 401));
        var scope = await access.GetScopeAsync(id, token);
        if (scope == SubscriptionAccessScope.None) return new(id, Error: IdentityEndpoints.Problem("subscription_required", 403));
        return new(id, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(securityVersion!))), scope == SubscriptionAccessScope.FullCatalog);
    }
    private static IResult Result<T>(CatalogResult<T> result) => result.Succeeded ? Results.Ok(result.Value) : IdentityEndpoints.Problem(result.Error!, result.Status, result.FieldErrors);
    private static bool TryQuery(HttpContext context, bool paging, out ConsumptionReportInterval? interval, out int page, out int pageSize)
    {
        interval = null; page = 1; pageSize = 20;
        var query = context.Request.Query;
        var allowed = paging ? new[] { "from", "to", "page", "pageSize" } : new[] { "from", "to" };
        if (query.Any(entry => !allowed.Contains(entry.Key, StringComparer.Ordinal) || entry.Value.Count != 1)) return false;
        if (!ConsumptionActivityRules.TryDate(query["from"].FirstOrDefault(), out var from) || !ConsumptionActivityRules.TryDate(query["to"].FirstOrDefault(), out var to)) return false;
        interval = new(from, to);
        if (paging && ((query.TryGetValue("page", out var number) && !TryNumber(number[0], out page)) ||
            (query.TryGetValue("pageSize", out var size) && !TryNumber(size[0], out pageSize)))) return false;
        return ConsumptionActivityRules.ValidInterval(interval) && ConsumptionActivityRules.ValidPage(page, pageSize);
    }
    private static bool TryNumber(string? value, out int number)
    {
        number = 0;
        return value is not null && Regex.IsMatch(value, @"\A[1-9][0-9]{0,6}\z", RegexOptions.CultureInvariant) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
