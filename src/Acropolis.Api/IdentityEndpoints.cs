using System.Security.Claims;
using Acropolis.Identity.Application;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Acropolis.Identity.Infrastructure;
using Microsoft.Extensions.Options;

public static class IdentityEndpoints
{
    public static void MapChannelIdentity(this WebApplication app)
    {
        var identity = app.MapGroup("/api/v1/identity");
        identity.AddEndpointFilter<CsrfFilter>();
        identity.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        });
        identity.MapPost("/register", async (RegisterRequest request, IIdentityService service, CancellationToken token) =>
        {
            var result = await service.RegisterAsync(request, token);
            return result.Succeeded ? Accepted() : Failure(result);
        }).RequireRateLimiting("identity-public");
        identity.MapPost("/login", async (LoginRequest request, IIdentityService service, HttpContext context, TimeProvider clock, CancellationToken token) =>
        {
            var result = await service.LoginAsync(request, token);
            if (!result.Succeeded) return Failure(result);
            var login = result.Value!;
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, login.User.Id.ToString()),
                new(ClaimTypes.Name, login.User.DisplayName),
                new("auth_version", login.SecurityVersion)
            };
            claims.AddRange(login.User.Permissions.Select(permission => new Claim("permission", permission)));
            await context.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme)),
                new AuthenticationProperties { IssuedUtc = clock.GetUtcNow(), ExpiresUtc = clock.GetUtcNow() + IdentityRules.SessionLifetime, IsPersistent = false, AllowRefresh = false });
            return Results.Ok(login.User);
        }).RequireRateLimiting("identity-public");
        identity.MapPost("/logout", async (HttpContext context) => { await context.SignOutAsync(IdentityConstants.ApplicationScheme); return Results.NoContent(); }).RequireAuthorization();
        identity.MapGet("/me", async (HttpContext context, IIdentityService service, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var user = await service.GetUserAsync(UserId(context), token);
            return user is null ? Problem("invalid_credentials", 401) : Results.Ok(user);
        }).RequireAuthorization();
        identity.MapPatch("/me", async (ProfileRequest request, HttpContext context, IIdentityService service, CancellationToken token) =>
        {
            var result = await service.UpdateProfileAsync(UserId(context), request, token);
            return result.Succeeded ? Results.Ok(result.Value) : Failure(result);
        }).RequireAuthorization();
        identity.MapPost("/confirm-email", async (ConfirmEmailRequest request, IIdentityService service, CancellationToken token) =>
        {
            var result = await service.ConfirmEmailAsync(request, token);
            return result.Succeeded ? Results.NoContent() : Failure(result);
        }).RequireRateLimiting("identity-public");
        identity.MapPost("/resend-confirmation", async (EmailRequest request, IIdentityService service, CancellationToken token) =>
        {
            await service.RequestEmailAsync(request.Email, "confirm", token);
            return Accepted();
        }).RequireRateLimiting("identity-public");
        identity.MapPost("/forgot-password", async (EmailRequest request, IIdentityService service, CancellationToken token) =>
        {
            await service.RequestEmailAsync(request.Email, "reset", token);
            return Accepted();
        }).RequireRateLimiting("identity-public");
        identity.MapPost("/reset-password", async (ResetPasswordRequest request, IIdentityService service, CancellationToken token) =>
        {
            var result = await service.ResetPasswordAsync(request, token);
            return result.Succeeded ? Results.NoContent() : Failure(result);
        }).RequireRateLimiting("identity-public");
        identity.MapPost("/change-password", async (ChangePasswordRequest request, HttpContext context, IIdentityService service, CancellationToken token) =>
        {
            var result = await service.ChangePasswordAsync(UserId(context), request, token);
            if (!result.Succeeded) return Failure(result);
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.NoContent();
        }).RequireAuthorization();

        var admin = app.MapGroup("/api/v1/admin/users").RequireAuthorization(IdentityRules.ManageUsers);
        admin.AddEndpointFilter<CsrfFilter>();
        admin.MapGet("/", async (string? search, string? status, string? level, int? page, int? pageSize, IIdentityService service, CancellationToken token) =>
        {
            var number = page ?? 1;
            var size = pageSize ?? 20;
            if (number < 1 || number > 1000000 || size is < 1 or > 100 || search?.Length > 100 || (status is not null && status is not ("active" or "disabled" or "pending")) || (level is not null && !IdentityRules.Levels.Contains(level))) return Problem("validation_error", 400);
            return Results.Ok(await service.ListUsersAsync(search, status, level, number, size, token));
        });
        admin.MapGet("/{id:guid}", async (Guid id, IIdentityService service, CancellationToken token) =>
        {
            var user = await service.GetUserAsync(id, token);
            return user is null ? Problem("not_found", 404) : Results.Ok(user);
        });
        admin.MapPatch("/{id:guid}", async (Guid id, AdminUserRequest request, HttpContext context, IIdentityService service, CancellationToken token) =>
        {
            var result = await service.UpdateUserAsync(UserId(context), id, request, token);
            return result.Succeeded ? Results.Ok(result.Value) : Failure(result);
        });
    }
    public static IResult Problem(string code, int status, Dictionary<string, string[]>? fields = null) =>
        Results.Problem(statusCode: status, title: "No se pudo completar la solicitud.", extensions:
            new Dictionary<string, object?> { ["code"] = code, ["fieldErrors"] = fields });
    private static IResult Accepted() => Results.Json(new { message = "Si corresponde, recibirás un correo con las instrucciones." }, statusCode: 202);
    private static IResult Failure<T>(IdentityResult<T> result) => Problem(result.Error!, result.Status, result.FieldErrors);
    private static Guid UserId(HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
public sealed class CsrfFilter(IAntiforgery antiforgery, IOptions<IdentitySettings> settings) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (HttpMethods.IsPost(context.HttpContext.Request.Method) || HttpMethods.IsPatch(context.HttpContext.Request.Method))
        {
            var request = context.HttpContext.Request;
            var origin = request.Headers.Origin.ToString();
            var expected = string.IsNullOrWhiteSpace(settings.Value.PublicOrigin)
                ? $"{request.Scheme}://{request.Host}" : settings.Value.PublicOrigin.TrimEnd('/');
            if (origin.Length > 0 && !string.Equals(origin.TrimEnd('/'), expected, StringComparison.OrdinalIgnoreCase))
                return IdentityEndpoints.Problem("csrf_invalid", 400);
            try { await antiforgery.ValidateRequestAsync(context.HttpContext); }
            catch (AntiforgeryValidationException) { return IdentityEndpoints.Problem("csrf_invalid", 400); }
        }
        return await next(context);
    }
}
