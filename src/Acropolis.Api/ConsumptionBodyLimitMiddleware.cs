using Microsoft.AspNetCore.Http.Features;

namespace Acropolis.Api;

// Both Kestrel and in-process/chunked hosts enforce the same bounded read.
public sealed class ConsumptionBodyLimitMiddleware(RequestDelegate next)
{
    public const int MaximumBytes = 8192;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.Path.StartsWithSegments("/api/v1/consumption"))
        {
            await next(context);
            return;
        }
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaximumBytes;
        if (context.Request.ContentLength > MaximumBytes)
        {
            await IdentityEndpoints.Problem("validation_error", StatusCodes.Status413PayloadTooLarge).ExecuteAsync(context);
            return;
        }
        var original = context.Request.Body;
        using var bounded = new MemoryStream(MaximumBytes + 1);
        var buffer = new byte[1024];
        try
        {
            while (bounded.Length <= MaximumBytes)
            {
                var remaining = MaximumBytes + 1 - (int)bounded.Length;
                var count = await original.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), context.RequestAborted);
                if (count == 0) break;
                await bounded.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await IdentityEndpoints.Problem("validation_error", StatusCodes.Status413PayloadTooLarge).ExecuteAsync(context);
            return;
        }
        if (bounded.Length > MaximumBytes)
        {
            await IdentityEndpoints.Problem("validation_error", StatusCodes.Status413PayloadTooLarge).ExecuteAsync(context);
            return;
        }
        bounded.Position = 0;
        context.Request.Body = bounded;
        try { await next(context); }
        finally { context.Request.Body = original; }
    }
}
