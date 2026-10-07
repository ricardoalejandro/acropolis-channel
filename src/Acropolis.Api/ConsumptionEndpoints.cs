using System.Security.Claims;
using Acropolis.Catalog.Application;
using Acropolis.Identity.Application;
using Acropolis.Subscriptions.Application;

public static class ConsumptionEndpoints
{
    public static void MapChannelConsumption(this WebApplication app)
    {
        var consumption = app.MapGroup("/api/v1/consumption").RequireAuthorization();
        consumption.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        consumption.MapGet("/content/{slug}", async (string slug, HttpContext context, IIdentityService identity, ISubscriptionAccess access, ICatalogService catalog, CancellationToken token) =>
        {
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return IdentityEndpoints.Problem("invalid_credentials", 401);
            var user = await identity.GetUserAsync(userId, token);
            if (user is null || !user.EmailConfirmed || user.Status != "active")
                return IdentityEndpoints.Problem("invalid_credentials", 401);
            var scope = await access.GetScopeAsync(userId, token);
            if (scope == SubscriptionAccessScope.None)
                return IdentityEndpoints.Problem("subscription_required", 403);
            if (!CatalogRules.ValidSlug(slug)) return IdentityEndpoints.Problem("not_found", 404);
            var work = await catalog.GetPublishedWorkAsync(slug, scope == SubscriptionAccessScope.FullCatalog, token);
            return work.Succeeded ? Results.Ok(work.Value) : IdentityEndpoints.Problem(work.Error!, work.Status);
        });
    }
}
