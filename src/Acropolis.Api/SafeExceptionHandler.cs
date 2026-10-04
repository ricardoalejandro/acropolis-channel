using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

internal sealed class SafeExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception is DbUpdateConcurrencyException ? 409 : exception is BadHttpRequestException ? 400 : exception is Npgsql.NpgsqlException ? 503 : 500;
        var code = status == 409 ? "concurrency_conflict" : status == 400 ? "validation_error" : "service_unavailable";
        await IdentityEndpoints.Problem(code, status).ExecuteAsync(context);
        return true;
    }
}
