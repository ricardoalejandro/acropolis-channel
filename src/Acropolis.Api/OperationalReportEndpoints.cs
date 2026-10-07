using Acropolis.Catalog.Application;
using Acropolis.Identity.Application;
using Acropolis.Subscriptions.Application;

public static class OperationalReportEndpoints
{
    public static void MapChannelOperationalReports(this WebApplication app)
    {
        var reports = app.MapGroup("/api/v1/admin/reports");
        reports.MapGet("/identity", async (HttpContext context, IIdentityReportService service, CancellationToken token) =>
        {
            if (context.Request.QueryString.HasValue) return IdentityEndpoints.Problem("validation_error", 400);
            return Results.Ok(await service.GetCurrentAsync(token));
        }).RequireAuthorization(IdentityRules.ManageUsers);
        reports.MapGet("/catalog", async (HttpContext context, ICatalogReportService service, CancellationToken token) =>
        {
            if (context.Request.QueryString.HasValue) return IdentityEndpoints.Problem("validation_error", 400);
            return Results.Ok(await service.GetCurrentAsync(token));
        }).RequireAuthorization(IdentityRules.ManageContent);
        reports.MapGet("/subscriptions", async (HttpContext context, ISubscriptionReportService service, CancellationToken token) =>
        {
            if (context.Request.QueryString.HasValue) return IdentityEndpoints.Problem("validation_error", 400);
            return Results.Ok(await service.GetCurrentAsync(token));
        }).RequireAuthorization(IdentityRules.ManageSubscriptions);
        reports.MapGet("/subscriptions/events", async (HttpContext context, ISubscriptionEventReportService service, CancellationToken token) =>
        {
            var query = context.Request.Query;
            if (query.Count != 2 || query.Keys.Any(key => key is not "from" and not "to")
                || !query.TryGetValue("from", out var from) || from.Count != 1
                || !query.TryGetValue("to", out var to) || to.Count != 1
                || !SubscriptionEventReportRules.TryParseInterval(from[0], to[0], out var interval))
                return IdentityEndpoints.Problem("validation_error", 400);
            return Results.Ok(await service.GetEventsAsync(interval!, token));
        }).RequireAuthorization(IdentityRules.ManageSubscriptions);
    }
}
