using System.Security.Claims;
using Acropolis.Identity.Application;
using Acropolis.Subscriptions.Application;

public static class SubscriptionEndpoints
{
    public static void MapChannelSubscriptions(this WebApplication app)
    {
        var own = app.MapGroup("/api/v1/subscriptions").RequireAuthorization();
        own.AddEndpointFilter<CsrfFilter>();
        own.MapGet("/me", async (HttpContext context, ISubscriptionService service, CancellationToken token) => Results.Ok(await service.GetMineAsync(UserId(context), token)));
        own.MapPost("/activate", async (ActivateSubscriptionRequest request, HttpContext context, ISubscriptionService service, CancellationToken token) => Result(await service.ActivateAsync(UserId(context), token))).RequireRateLimiting("subscriptions-write");
        own.MapPost("/cancel", async (CancelSubscriptionRequest request, HttpContext context, ISubscriptionService service, CancellationToken token) => Result(await service.CancelAsync(UserId(context), request, token))).RequireRateLimiting("subscriptions-write");
        var admin = app.MapGroup("/api/v1/admin/subscriptions").RequireAuthorization(IdentityRules.ManageSubscriptions);
        admin.AddEndpointFilter<CsrfFilter>();
        admin.MapGet("/", async (string? status, Guid? userId, int? page, int? pageSize, ISubscriptionService service, CancellationToken token) =>
        {
            var number = page ?? 1; var size = pageSize ?? 20;
            if (!SubscriptionRules.ValidPage(number, size) || (status is not null && !SubscriptionRules.Statuses.Contains(status))) return IdentityEndpoints.Problem("validation_error", 400);
            return Results.Ok(await service.ListAsync(status, userId, number, size, token));
        });
        admin.MapGet("/accounts", async (string? search, int? page, int? pageSize, IIdentityService accounts, CancellationToken token) =>
        {
            var number = page ?? 1; var size = pageSize ?? 20;
            if (!SubscriptionRules.ValidPage(number, size) || search?.Length > 200 || search?.Any(char.IsControl) == true) return IdentityEndpoints.Problem("validation_error", 400);
            var result = await accounts.ListUsersAsync(search, null, null, number, size, token);
            return Results.Ok(new SubscriptionAccountPage(result.Items.Select(x => new SubscriptionAccountView(x.Id, x.DisplayName, x.Email, x.Status, x.EmailConfirmed)).ToArray(), result.Total, result.Page, result.PageSize));
        });
        admin.MapGet("/accounts/lookup", async (string? userIds, IIdentityService accounts, CancellationToken token) =>
        {
            if (!TryLookupIds(userIds, out var ids)) return IdentityEndpoints.Problem("validation_error", 400);
            return Results.Ok(await accounts.LookupAccountsAsync(ids, token));
        });
        admin.MapGet("/audit", async (DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? action, Guid? subscriptionId, Guid? userId, int? page, int? pageSize, ISubscriptionService service, CancellationToken token) =>
        {
            var number = page ?? 1; var size = pageSize ?? 20;
            if (!SubscriptionRules.ValidAudit(fromUtc, toUtc, action, number, size)) return IdentityEndpoints.Problem("validation_error", 400);
            return Results.Ok(await service.ListAuditAsync(fromUtc, toUtc, action, subscriptionId, userId, number, size, token));
        });
        admin.MapGet("/{id:guid}", async (Guid id, ISubscriptionService service, CancellationToken token) =>
        {
            var view = await service.GetAsync(id, token);
            return view is null ? IdentityEndpoints.Problem("not_found", 404) : Results.Ok(view);
        });
        admin.MapPatch("/{id:guid}", async (Guid id, AdminSubscriptionRequest request, HttpContext context, ISubscriptionService service, CancellationToken token) => Result(await service.UpdateAsync(UserId(context), id, request, token)));
    }
    private static bool TryLookupIds(string? value, out Guid[] ids)
    {
        ids = [];
        if (string.IsNullOrEmpty(value) || value.Length > 739) return false;
        var parts = value.Split(',');
        if (parts.Length is < 1 or > 20) return false;
        var parsed = new Guid[parts.Length];
        var unique = new HashSet<Guid>();
        for (var index = 0; index < parts.Length; index++)
        {
            if (!Guid.TryParseExact(parts[index], "D", out parsed[index]) || parsed[index] == Guid.Empty || !unique.Add(parsed[index])) return false;
        }
        ids = parsed;
        return true;
    }
    private static Guid UserId(HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static IResult Result<T>(SubscriptionResult<T> result) => result.Succeeded ? Results.Ok(result.Value) : IdentityEndpoints.Problem(result.Error!, result.Status);
}
