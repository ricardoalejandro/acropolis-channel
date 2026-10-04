using System.Security.Claims;
using Acropolis.Identity.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

public static class MfaEndpoints
{
    public static void MapChannelMfa(this WebApplication app)
    {
        var mfa = app.MapGroup("/api/v1/identity/mfa").RequireRateLimiting("identity-public");
        mfa.AddEndpointFilter<CsrfFilter>();
        mfa.MapGet("/", async (HttpContext context, IMfaService service, CancellationToken token) =>
        {
            var result = await service.StatusAsync(UserId(context)!.Value, token);
            return result.Succeeded ? Results.Ok(result.Value) : Failure(result);
        }).RequireAuthorization();
        mfa.MapPost("/enrollment", async (MfaEnrollmentRequest request, HttpContext context, IMfaService service, CancellationToken token) =>
        {
            var result = await service.StartEnrollmentAsync(UserId(context), request, token);
            return result.Succeeded ? Results.Ok(result.Value) : Failure(result);
        });
        mfa.MapPost("/enable", async (MfaEnableRequest request, HttpContext context, IMfaService service, TimeProvider clock, CancellationToken token) =>
        {
            var result = await service.EnableAsync(request, token);
            if (!result.Succeeded) return Failure(result);
            await IdentityEndpoints.SignInUser(context, result.Value!.Authentication, clock, true);
            return Results.Ok(new { user = result.Value.Authentication.User, recoveryCodes = result.Value.RecoveryCodes });
        });
        mfa.MapPost("/challenge", async (MfaVerifyRequest request, HttpContext context, IMfaService service, TimeProvider clock, CancellationToken token) =>
        {
            var result = await service.VerifyAsync(request, token);
            if (!result.Succeeded) return Failure(result);
            await IdentityEndpoints.SignInUser(context, result.Value!, clock, true);
            return Results.Ok(result.Value!.User);
        });
        mfa.MapPost("/recovery-codes", async (MfaReauthenticateRequest request, HttpContext context, IMfaService service, CancellationToken token) =>
        {
            var result = await service.RegenerateAsync(UserId(context)!.Value, request, token);
            if (!result.Succeeded) return Failure(result);
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.Ok(new { recoveryCodes = result.Value });
        }).RequireAuthorization();
        mfa.MapPost("/disable", async (MfaReauthenticateRequest request, HttpContext context, IMfaService service, CancellationToken token) =>
        {
            var result = await service.DisableAsync(UserId(context)!.Value, request, token);
            if (!result.Succeeded) return Failure(result);
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.NoContent();
        }).RequireAuthorization();
    }
    private static Guid? UserId(HttpContext context) => Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private static IResult Failure<T>(IdentityResult<T> result) => IdentityEndpoints.Problem(result.Error!, result.Status, result.FieldErrors);
}
