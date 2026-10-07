using System.Security.Claims;
using Acropolis.Catalog.Application;
using Acropolis.Identity.Application;

public static class TopicEndpoints
{
    public static void MapChannelTopics(this WebApplication app)
    {
        var topics = app.MapGroup("/api/v1/admin/catalog/topics").RequireAuthorization(IdentityRules.ManageContent);
        topics.AddEndpointFilter<CsrfFilter>();
        topics.MapGet("/", async (string? search, string? status, int? page, int? pageSize, ITopicService service, CancellationToken token) =>
        {
            var number = page ?? 1; var size = pageSize ?? 20;
            var errors = TopicRules.ValidateQuery(search, status, number, size);
            return errors.Count > 0 ? IdentityEndpoints.Problem("validation_error", 400, errors) : Results.Ok(await service.ListAdminAsync(search, status, number, size, token));
        });
        topics.MapGet("/{id:guid}", async (Guid id, ITopicService service, CancellationToken token) =>
        {
            var result = await service.GetAdminAsync(id, token);
            return result is null ? IdentityEndpoints.Problem("not_found", 404) : Results.Ok(result);
        });
        topics.MapGet("/{id:guid}/audit", async (Guid id, int? page, int? pageSize, ITopicService service, CancellationToken token) =>
        {
            var number = page ?? 1; var size = pageSize ?? 20;
            var errors = TopicRules.ValidateQuery(null, null, number, size);
            if (errors.Count > 0) return IdentityEndpoints.Problem("validation_error", 400, errors);
            var result = await service.ListAuditAsync(id, number, size, token);
            return result is null ? IdentityEndpoints.Problem("not_found", 404) : Results.Ok(result);
        });
        topics.MapPost("/", async (CreateTopicRequest request, ITopicService service, HttpContext context, CancellationToken token) =>
        {
            var result = await service.CreateAsync(Actor(context), request, token);
            return result.Succeeded ? Results.Created($"/api/v1/admin/catalog/topics/{result.Value!.Id}", result.Value) : Problem(result);
        });
        topics.MapPut("/{id:guid}", async (Guid id, UpdateTopicRequest request, ITopicService service, HttpContext context, CancellationToken token) =>
            Result(await service.UpdateAsync(Actor(context), id, request, token)));
        topics.MapPut("/{id:guid}/state", async (Guid id, TopicStateRequest request, ITopicService service, HttpContext context, CancellationToken token) =>
            Result(await service.SetStateAsync(Actor(context), id, request, token)));
        topics.MapPut("/order", async (MoveTopicRequest request, ITopicService service, HttpContext context, CancellationToken token) =>
            Result(await service.MoveAsync(Actor(context), request, token)));
        var content = app.MapGroup("/api/v1/admin/content").RequireAuthorization(IdentityRules.ManageContent);
        content.AddEndpointFilter<CsrfFilter>();
        content.MapGet("/{id:guid}/topics", async (Guid id, ITopicService service, CancellationToken token) =>
        {
            var result = await service.GetContentTopicsAsync(id, token);
            return result is null ? IdentityEndpoints.Problem("not_found", 404) : Results.Ok(result);
        });
        content.MapPut("/{id:guid}/topics", async (Guid id, AssignContentTopicsRequest request, ITopicService service, HttpContext context, CancellationToken token) =>
            Result(await service.AssignAsync(Actor(context), id, request, token)));
        var catalog = app.MapGroup("/api/v1/catalog/topics");
        catalog.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "no-store"; return await next(context); });
        catalog.MapGet("/", async (int? page, int? pageSize, ITopicService service, CancellationToken token) =>
        {
            var number = page ?? 1; var size = pageSize ?? 20;
            var errors = TopicRules.ValidateQuery(null, null, number, size);
            return errors.Count > 0 ? IdentityEndpoints.Problem("validation_error", 400, errors) : Results.Ok(await service.ListPublicAsync(number, size, token));
        });
    }
    private static Guid Actor(HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static IResult Result<T>(CatalogResult<T> result) => result.Succeeded ? Results.Ok(result.Value) : Problem(result);
    private static IResult Problem<T>(CatalogResult<T> result) => IdentityEndpoints.Problem(result.Error!, result.Status, result.FieldErrors);
}
