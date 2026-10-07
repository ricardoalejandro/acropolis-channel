using Acropolis.Identity.Application;

public static class AccountAccessEndpoints
{
    public static void MapChannelAccountAccess(this WebApplication app)
    {
        app.MapGet("/api/v1/admin/users/{id:guid}/access", async (Guid id, IAccountAccessService service, CancellationToken token) =>
        {
            var access = await service.GetAsync(id, token);
            return access is null ? IdentityEndpoints.Problem("not_found", 404) : Results.Ok(access);
        }).RequireAuthorization(IdentityRules.ManageUsers);
    }
}
