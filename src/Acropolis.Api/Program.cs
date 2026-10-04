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
builder.Services.AddHostedService<DatabaseConfigurationValidator>();
builder.Services.AddSingleton<GreetingService>();
builder.Services.AddScoped<IPlatformStateReader>(services =>
    new PostgresPlatformStateReader(services.GetRequiredService<IConfiguration>().GetConnectionString("Database")!));
builder.Services.AddScoped<ReadinessService>(services =>
    new ReadinessService(services.GetRequiredService<IPlatformStateReader>(), TimeSpan.FromSeconds(2)));

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
    await next(context);
});
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseDefaultFiles();
app.UseStaticFiles();
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
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
