using System.Security.Claims;
using Acropolis.Catalog.Application;
using Acropolis.Identity.Application;

public static class CatalogEndpoints
{
    public static void MapChannelCatalog(this WebApplication app)
    {
        var catalog = app.MapGroup("/api/v1/catalog");
        catalog.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        catalog.MapGet("/categories", () => Results.Ok(new { items = CatalogRules.Categories }));
        catalog.MapGet("/content", async (string? search, string? category, int? page, int? pageSize, ICatalogService service, CancellationToken token) =>
        {
            var number = page ?? 1; var size = pageSize ?? 20;
            var errors = CatalogRules.ValidateQuery(search, category, null, number, size);
            return errors.Count > 0 ? IdentityEndpoints.Problem("validation_error", 400, errors)
                : Results.Ok(await service.ListPublishedAsync(search, category, number, size, token));
        });
        catalog.MapGet("/content/{slug}", async (string slug, ICatalogService service, CancellationToken token) =>
        {
            if (!CatalogRules.ValidSlug(slug)) return IdentityEndpoints.Problem("not_found", 404);
            var entry = await service.GetPublishedAsync(slug, token);
            return entry is null ? IdentityEndpoints.Problem("not_found", 404) : Results.Ok(entry);
        });
        var admin = app.MapGroup("/api/v1/admin/content").RequireAuthorization(IdentityRules.ManageContent);
        admin.AddEndpointFilter<CsrfFilter>();
        admin.MapGet("/", async (string? search, string? category, string? status, int? page, int? pageSize, ICatalogService service, CancellationToken token) =>
        {
            var number = page ?? 1; var size = pageSize ?? 20;
            var errors = CatalogRules.ValidateQuery(search, category, status, number, size);
            return errors.Count > 0 ? IdentityEndpoints.Problem("validation_error", 400, errors)
                : Results.Ok(await service.ListAdminAsync(search, category, status, number, size, token));
        });
        admin.MapGet("/{id:guid}", async (Guid id, ICatalogService service, CancellationToken token) =>
        {
            var entry = await service.GetAdminAsync(id, token);
            return entry is null ? IdentityEndpoints.Problem("not_found", 404) : Results.Ok(entry);
        });
        admin.MapPost("/", async (CreateContentRequest request, ICatalogService service, HttpContext context, CancellationToken token) =>
        {
            var result = await service.CreateAsync(UserId(context), request, token);
            return result.Succeeded ? Results.Created($"/api/v1/admin/content/{result.Value!.Id}", result.Value)
                : IdentityEndpoints.Problem(result.Error!, result.Status, result.FieldErrors);
        });
        admin.MapPut("/{id:guid}", async (Guid id, UpdateContentRequest request, ICatalogService service, HttpContext context, CancellationToken token) =>
        {
            var result = await service.UpdateAsync(UserId(context), id, request, token);
            return result.Succeeded ? Results.Ok(result.Value)
                : IdentityEndpoints.Problem(result.Error!, result.Status, result.FieldErrors);
        });
    }
    private static Guid UserId(HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
