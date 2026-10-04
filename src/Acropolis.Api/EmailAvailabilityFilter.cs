using Acropolis.Identity.Infrastructure;
using Microsoft.Extensions.Options;

public sealed class EmailAvailabilityFilter(IOptions<IdentitySettings> settings) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        settings.Value.EmailEnabled ? next(context) : ValueTask.FromResult<object?>(IdentityEndpoints.Problem("email_unavailable", 503));
}
