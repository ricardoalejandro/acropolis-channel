using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.ResponseCompression;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Acropolis.Platform.Application;
using Acropolis.Platform.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.WebHost.ConfigureKestrel(server => server.AddServerHeader = false);
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});

builder.Services.AddOpenApi();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ["text/css", "text/javascript", "application/javascript"];
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddExceptionHandler<SafeExceptionHandler>();
builder.Services.AddChannelIdentity(builder.Configuration);
builder.Services.AddHostedService<IdentityConfigurationValidator>();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddCookie(IdentityConstants.ApplicationScheme, options =>
{
    options.Cookie.Name = "__Host-acropolis-session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.Path = "/";
    options.ExpireTimeSpan = IdentityRules.SessionLifetime;
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => IdentityEndpoints.Problem("invalid_credentials", 401).ExecuteAsync(context.HttpContext);
    options.Events.OnRedirectToAccessDenied = context => IdentityEndpoints.Problem("forbidden", 403).ExecuteAsync(context.HttpContext);
});
builder.Services.AddAuthorization(options => options.AddPolicy(IdentityRules.ManageUsers, policy => policy.RequireAuthenticatedUser().RequireClaim("permission", IdentityRules.ManageUsers)));
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-acropolis-csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var proxy in (builder.Configuration["Identity:KnownProxies"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (IPAddress.TryParse(proxy, out var address)) options.KnownProxies.Add(address);
    if (options.KnownProxies.Count == 0) options.ForwardedHeaders = ForwardedHeaders.None;
});
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = (context, _) => new ValueTask(IdentityEndpoints.Problem("rate_limited", 429).ExecuteAsync(context.HttpContext));
    options.AddPolicy("identity-public", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});

builder.Services.AddHostedService<DatabaseConfigurationValidator>();
builder.Services.AddSingleton<GreetingService>();
builder.Services.AddScoped<IPlatformStateReader>(services =>
    new CompositeDatabaseReader(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")!));
builder.Services.AddScoped<ReadinessService>(services =>
    new ReadinessService(services.GetRequiredService<IPlatformStateReader>(), TimeSpan.FromSeconds(2)));

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/identity") || context.Request.Path.StartsWithSegments("/api/v1/admin")) context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
    await next(context);
});
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapChannelIdentity();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseWhen(context => context.Request.Path.StartsWithSegments("/assets"), branch => branch.UseResponseCompression());
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var fingerprinted = context.Context.Request.Path.StartsWithSegments("/assets")
            && Regex.IsMatch(context.File.Name, @"-[A-Za-z0-9_-]{8,}\.(?:js|css|woff2?|svg|png|webp|avif)$", RegexOptions.CultureInvariant);
        context.Context.Response.Headers.CacheControl = fingerprinted
            ? "public, max-age=31536000, immutable"
            : "no-cache";
    }
});
app.MapGet("/api/v1/greeting", (GreetingService service) => Results.Ok(service.GetGreeting()));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (ReadinessService service, CancellationToken cancellationToken) =>
{
    var result = await service.CheckAsync(cancellationToken);
    return Results.Json(new { status = result.Status }, statusCode: result.IsReady ? 200 : 503);
});
app.Map("/api/{**path}", () => Results.Problem(statusCode: 404, title: "Not Found"));
app.Map("/health/{**path}", () => Results.Problem(statusCode: 404, title: "Not Found"));
if (!app.Environment.IsDevelopment())
{
    app.Map("/openapi/{**path}", () => Results.Problem(statusCode: 404, title: "Not Found"));
}
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
app.Run();

public partial class Program;
