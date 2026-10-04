using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Acropolis.Identity.Infrastructure;

// CookieAuthenticationHandler renews its cached session key when signing in again.
// Reauthentication must replace that key rather than elevate a previously copied cookie.
internal static class SessionRotation
{
    private const string SessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";
    private static readonly object Rotation = new();

    internal static async Task BeginAsync(CookieSigningInContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true) return;
        var cookie = context.Options.CookieManager.GetRequestCookie(context.HttpContext, context.Options.Cookie.Name!);
        var previous = cookie is null ? null : context.Options.TicketDataFormat.Unprotect(cookie, Binding(context.HttpContext));
        var key = previous?.Principal.FindFirstValue(SessionIdClaim);
        if (key is null) return;
        await context.Options.SessionStore!.RemoveAsync(key, context.HttpContext, context.HttpContext.RequestAborted);
        context.HttpContext.Items[Rotation] = true;
    }

    internal static async Task CompleteAsync(CookieSignedInContext context)
    {
        if (!context.HttpContext.Items.ContainsKey(Rotation)) return;
        var ticket = new AuthenticationTicket(context.Principal!, context.Properties, context.Scheme.Name);
        var key = await context.Options.SessionStore!.StoreAsync(ticket, context.HttpContext, context.HttpContext.RequestAborted);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(SessionIdClaim, key)], context.Options.ClaimsIssuer));
        var cookieTicket = new AuthenticationTicket(principal, null, context.Scheme.Name);
        var cookie = context.Options.TicketDataFormat.Protect(cookieTicket, Binding(context.HttpContext));
        var options = context.Options.Cookie.Build(context.HttpContext);
        options.Expires = context.Properties.IsPersistent ? context.Properties.ExpiresUtc?.UtcDateTime : null;
        // AppendResponseCookie appends headers; replace only the obsolete session reference.
        var cookiePrefix = context.Options.Cookie.Name + "=";
        context.Response.Headers.SetCookie = new StringValues(context.Response.Headers.SetCookie
            .Where(header => header is not null && !header.StartsWith(cookiePrefix, StringComparison.Ordinal)).ToArray());
        context.Options.CookieManager.AppendResponseCookie(context.HttpContext, context.Options.Cookie.Name!, cookie, options);
    }
    private static string? Binding(Microsoft.AspNetCore.Http.HttpContext context)
    {
        var binding = context.Features.Get<ITlsTokenBindingFeature>()?.GetProvidedTokenBindingId();
        return binding is null ? null : Convert.ToBase64String(binding);
    }
}
